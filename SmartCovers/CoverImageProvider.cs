using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PDFtoImage;
using SharpCompress.Archives;
using SkiaSharp;
using SmartCovers.Configuration;

namespace SmartCovers;

/// <summary>
/// Fallback cover provider for books, comics and audiobooks. Handles PDFs (first
/// page rendered via built-in PDFium), EPUBs (aggressive image search inside the
/// ZIP archive), comic archives (.cbz/.cbr — first page in natural sort order),
/// and audio files (embedded art extraction via ffmpeg raw stream copy).
/// Acts as a safety net when built-in providers fail — particularly for audio
/// files with mislabeled codec tags (e.g. JPEG data tagged as PNG in ID3).
/// </summary>
public class CoverImageProvider : IDynamicImageProvider
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".tiff", ".tif"
    };

    private static readonly HashSet<string> CoverFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "cover", "portada", "front", "frontcover", "front_cover", "book_cover"
    };

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".m4a", ".m4b", ".flac", ".ogg", ".opus", ".wma", ".aac", ".wav"
    };

    private static readonly HashSet<string> ComicExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cbz", ".cbr"
    };

    private static readonly HashSet<string> MobiExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mobi", ".azw", ".azw3", ".prc"
    };

    // Image names that mark a file as the cover wherever it appears in the name,
    // so "CoverArt.jpg" and "Make Time-Cover.jpg" are found as well as "cover.jpg".
    private static readonly string[] CoverNameHints = ["cover", "portada", "front", "poster", "folder"];

    // Comic candidates are limited to formats DetectImageFormat can verify: an
    // extension it can never validate (.tif/.tiff) would only waste candidate
    // attempts and can never ship as a cover anyway. (The EPUB path keeps the
    // wider ImageExtensions set — it trusts extensions by design.)
    private static readonly HashSet<string> ComicImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp"
    };

    // Upper bound on how many archive entries a single comic extraction may open.
    // The cover is virtually always the first candidate; the cap keeps a
    // pathological archive (thousands of junk "images") from stalling a scan.
    private const int MaxComicCandidateAttempts = 5;

    // Decompression-bomb guard: hard ceiling on the decompressed size of a single
    // comic entry. Real cover pages are a few MB; a crafted archive must not be
    // able to balloon Jellyfin's memory (headers can lie, so the cap is enforced
    // while copying, not just checked up front).
    private const long MaxComicEntryBytes = 64 * 1024 * 1024;

    // PDF availability cache as a tri-state: 0 = not yet probed, 1 = unavailable,
    // 2 = available. A volatile int gives an atomic, correctly-published read on the
    // lock-free fast path (weak memory models: arm64 — Raspberry Pi, Apple Silicon),
    // unlike a non-atomic Nullable<bool> struct.
    private const int PdfStateUnknown = 0;
    private const int PdfStateUnavailable = 1;
    private const int PdfStateAvailable = 2;

    // Every track of a folder audiobook asks for the same book's cover. Without a
    // cache a 100-track rip runs 100 identical online lookups (and 100 ffmpeg
    // probes). A handful of entries is enough: a scan walks a book's tracks
    // together. Negative results are cached too — those are the expensive ones.
    private const int MaxCachedFolders = 4;
    private const int MaxCachedCoverBytes = 4 * 1024 * 1024;

    private readonly ILogger<CoverImageProvider> _logger;
    private readonly OnlineCoverFetcher _onlineFetcher;
    private readonly CoverOutcomeLog? _outcomes;
    private readonly ILibraryManager? _libraryManager;
    private readonly object _generatedLock = new();
    private string? _generatedKey;
    private byte[]? _generatedBytes;
    private bool _noFontWarned;
    private readonly object _folderCoverLock = new();
    private readonly Dictionary<string, CachedCover> _folderCovers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _folderCoverOrder = new();
    private readonly object _pdfProbeLock = new();
    private readonly Func<bool> _pdfiumNativeProbe;
    private volatile int _pdfRenderingState;
    private string? _ffmpegPath;
    private bool _ffmpegChecked;

    /// <summary>
    /// Initializes a new instance of the <see cref="CoverImageProvider"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="onlineFetcher">The online cover fetcher.</param>
    /// <param name="outcomes">Where each request's outcome is left for the scheduled task.</param>
    /// <param name="libraryManager">Jellyfin's library, to read a book folder's poster and a book's authors.</param>
    [ActivatorUtilitiesConstructor]
    public CoverImageProvider(
        ILogger<CoverImageProvider> logger,
        OnlineCoverFetcher onlineFetcher,
        CoverOutcomeLog outcomes,
        ILibraryManager libraryManager)
        : this(logger, onlineFetcher, null, outcomes, libraryManager)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CoverImageProvider"/> class with an
    /// injectable native-availability probe. The <paramref name="pdfiumNativeProbe"/> seam
    /// exists so tests can assert the no-native path without loading real pdfium (and
    /// without ever constructing PDFtoImage's finalizable <c>PdfLibrary</c>).
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="onlineFetcher">The online cover fetcher.</param>
    /// <param name="pdfiumNativeProbe">
    /// A probe returning <see langword="true"/> when the pdfium native library is loadable.
    /// When <see langword="null"/>, the real <see cref="PdfiumNativeLibrary.TryLoad"/> probe is used.
    /// </param>
    /// <param name="outcomes">Where outcomes are recorded, or null.</param>
    /// <param name="libraryManager">Jellyfin's library, or null when not wired up.</param>
    internal CoverImageProvider(
        ILogger<CoverImageProvider> logger,
        OnlineCoverFetcher onlineFetcher,
        Func<bool>? pdfiumNativeProbe = null,
        CoverOutcomeLog? outcomes = null,
        ILibraryManager? libraryManager = null)
    {
        _logger = logger;
        _onlineFetcher = onlineFetcher;
        _pdfiumNativeProbe = pdfiumNativeProbe ?? DefaultPdfiumNativeProbe;
        _outcomes = outcomes;
        _libraryManager = libraryManager;
    }

    /// <summary>
    /// Where the plugin settings come from. Tests replace it, so they never touch
    /// the shared plugin instance.
    /// </summary>
    internal Func<PluginConfiguration?> ConfigurationSource { get; set; } = static () => Plugin.Instance?.Configuration;

    private PluginConfiguration? Configuration => ConfigurationSource();

    /// <summary>
    /// The default pdfium availability probe: loads (and pins) the native via the shared
    /// <see cref="PdfiumNativeLibrary"/>. It touches NO PDFtoImage type, so a failed probe
    /// never leaves a finalizable <c>PdfLibrary</c> behind (see that type for the full
    /// finalizer-crash rationale).
    /// </summary>
    private static bool DefaultPdfiumNativeProbe() => PdfiumNativeLibrary.TryLoad(out _);

    /// <summary>
    /// The provider name Jellyfin stores in a library's ImageFetchers list.
    /// </summary>
    internal const string ProviderName = "SmartCovers";

    /// <inheritdoc />
    public string Name => ProviderName;

    /// <inheritdoc />
    /// <remarks>
    /// Plain folders are included because a multi-file audiobook IS a folder in
    /// Jellyfin — the tracks are separate items and the folder is what the library
    /// grid shows. The test is on the exact type: nearly everything with children
    /// derives from <see cref="Folder"/> (Season, Series, BoxSet, MusicArtist,
    /// Playlist, CollectionFolder, the library roots), and none of those wants a
    /// book cover. <see cref="GetImage"/> then refuses any folder holding more than
    /// one book, so a shelf never gets a guessed cover either.
    /// </remarks>
    public bool Supports(BaseItem item) =>
        item is Book || item is AudioBook || item is Audio || item is MusicAlbum
        || item.GetType() == typeof(Folder);

    /// <inheritdoc />
    public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
    {
        yield return ImageType.Primary;
    }

    /// <inheritdoc />
    public async Task<DynamicImageResponse> GetImage(BaseItem item, ImageType type, CancellationToken cancellationToken)
    {
        var attempt = new CoverAttempt();
        var result = await GetImageCore(item, attempt, cancellationToken).ConfigureAwait(false);

        if (result.HasImage)
        {
            if (attempt.Reason is not (CoverReason.FoundOnline or CoverReason.InheritedFromFolder))
            {
                attempt.Reason = CoverReason.FoundInFile;
            }
        }
        else
        {
            result = MaybeGenerate(item, attempt);
        }

        _outcomes?.Record(item.Id, attempt.ToOutcome());
        return result;
    }

    private async Task<DynamicImageResponse> GetImageCore(BaseItem item, CoverAttempt attempt, CancellationToken cancellationToken)
    {
        var path = item.Path;

        if (string.IsNullOrEmpty(path))
        {
            return await GetOnlineCover(item, null, attempt, cancellationToken).ConfigureAwait(false);
        }

        var isDirectory = Directory.Exists(path);

        // A folder audiobook's identity is its folder name, never the track name
        // ("01", "Pista 1"). For a disc folder ("CD 3", "<title> CD 3") it is the
        // folder above. Null means the item has no single book folder.
        var libraryRoot = GetLibraryRootPath(item);
        var identityDir = BookIdentity.ResolveIdentityDirectory(
            isDirectory ? path : Path.GetDirectoryName(path),
            libraryRoot);

        if (isDirectory)
        {
            // A folder holding more than one book — a library root, an author shelf —
            // gets nothing. Guessing one cover for a shelf is worse than a blank tile.
            if (identityDir == null)
            {
                _logger.LogDebug("Not a single-book folder, skipping: {Path}", path);
                attempt.Reason = CoverReason.NotOneBook;
                return new DynamicImageResponse { HasImage = false };
            }

            return await GetBookFolderCover(identityDir, item, attempt, cancellationToken).ConfigureAwait(false);
        }

        DynamicImageResponse result;
        var ext = Path.GetExtension(path);
        var isTrack = AudioExtensions.Contains(ext);

        if (string.Equals(ext, ".epub", StringComparison.OrdinalIgnoreCase))
        {
            result = await GetEpubCover(path, cancellationToken).ConfigureAwait(false);
        }
        else if (ComicExtensions.Contains(ext))
        {
            result = await GetComicCover(path, cancellationToken).ConfigureAwait(false);
        }
        else if (string.Equals(ext, ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            result = await GetPdfCover(path, cancellationToken).ConfigureAwait(false);
        }
        else if (MobiExtensions.Contains(ext))
        {
            result = await GetMobiCover(path, cancellationToken).ConfigureAwait(false);
        }
        else if (isTrack)
        {
            result = await GetAudioCover(path, cancellationToken).ConfigureAwait(false);
            if (!result.HasImage)
            {
                result = await GetSidecarImage(path, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            result = new DynamicImageResponse { HasImage = false };
        }

        if (result.HasImage)
        {
            return result;
        }

        // A track with no art of its own takes the book's cover. Only tracks: a PDF
        // or a .cbr IS its own book, and a folder can hold many of those.
        if (isTrack && identityDir != null && IsBookLike(item))
        {
            return await GetBookFolderCover(identityDir, item, attempt, cancellationToken).ConfigureAwait(false);
        }

        // Final fallback: fetch the cover from online sources. Books and audiobooks
        // only — Open Library and Google Books are irrelevant for music.
        if (IsBookLike(item))
        {
            return await GetOnlineCover(item, null, attempt, cancellationToken).ConfigureAwait(false);
        }

        // Music: never looked up online. A track that belongs to no album may get
        // a drawn cover: a loose file in the library's top folder, or a track in a
        // folder Jellyfin did not make an album because it collects songs of many
        // albums ("WCYR-Base/Radiohead - Videotape.flac").
        if (isTrack)
        {
            (attempt.Title, attempt.Author) = OnlineCoverFetcher.ParseMusicTrack(item);
            attempt.LooseMusic = LibraryContext.IsSameDirectory(Path.GetDirectoryName(path), libraryRoot)
                || IsInPlainFolder(item);
        }

        return result;
    }


    /// <summary>
    /// True for the item types an online book catalogue can answer for. Note that
    /// <see cref="AudioBook"/> derives from <see cref="Audio"/>, so an "is not Audio"
    /// test would silently exclude every audiobook.
    /// </summary>
    private static bool IsBookLike(BaseItem item)
        => item is Book || item is AudioBook || item.GetType() == typeof(Folder);

    /// <summary>
    /// The library folder on disk this item belongs to, used as a hard ceiling on
    /// folder climbing. Returns null when it cannot be resolved, in which case only
    /// the structural rules apply.
    /// </summary>
    private string? GetLibraryRootPath(BaseItem item) => LibraryContext.RootPath(item, _libraryManager);

    private bool IsBookLibrary(BaseItem item) => LibraryContext.IsBookLibrary(item, _libraryManager);

    /// <summary>
    /// True when the item's parent is a plain folder, not a music album. An unknown
    /// parent counts as an album, so nothing is drawn on a guess.
    /// </summary>
    private bool IsInPlainFolder(BaseItem item)
    {
        if (_libraryManager == null || item.ParentId == Guid.Empty)
        {
            return false;
        }

        try
        {
            var parent = _libraryManager.GetItemById(item.ParentId);
            return parent != null && parent.GetType() == typeof(Folder);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not read the parent of '{Name}'", item.Name);
            return false;
        }
    }

    /// <summary>
    /// The cover for one book folder: images on disk first, then the folder's own
    /// Jellyfin poster, then online. Cached, so the other ninety-nine tracks of the
    /// same book cost nothing.
    /// </summary>
    private async Task<DynamicImageResponse> GetBookFolderCover(
        string identityDir, BaseItem item, CoverAttempt attempt, CancellationToken cancellationToken)
    {
        if (TryGetCachedCover(identityDir, attempt, out var cached))
        {
            return cached;
        }

        var result = await GetFolderAudioCover(identityDir, cancellationToken).ConfigureAwait(false);

        // Jellyfin often already holds a poster for the book folder (fetched or set
        // by hand) but never hands it down to the tracks, so each track would show a
        // blank tile. Take the folder's poster.
        if (!result.HasImage)
        {
            result = await GetFolderItemPoster(identityDir, item, cancellationToken).ConfigureAwait(false);
            if (result.HasImage)
            {
                attempt.Reason = CoverReason.InheritedFromFolder;
            }
        }

        // Only ask online for a folder that IS one book. A folder holding a stack of
        // PDFs or comics gets whatever image is already on disk and nothing more —
        // each of those files is its own book and carries its own cover.
        if (!result.HasImage && IsBookLike(item))
        {
            if (RepresentsOneBook(identityDir))
            {
                result = await GetOnlineCover(item, identityDir, attempt, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                attempt.Reason = CoverReason.NotOneBook;
            }
        }

        // A catalogue that could not be reached proves nothing: ask again next time.
        if (attempt.Reason == CoverReason.NetworkError)
        {
            return result;
        }

        return StoreCover(identityDir, result, attempt);
    }

    /// <summary>
    /// The Primary image Jellyfin already holds for the book folder's own item,
    /// when it is a local file and not a drawn cover.
    /// </summary>
    private async Task<DynamicImageResponse> GetFolderItemPoster(
        string identityDir, BaseItem item, CancellationToken cancellationToken)
    {
        var noImage = new DynamicImageResponse { HasImage = false };
        if (_libraryManager == null)
        {
            return noImage;
        }

        try
        {
            var folder = _libraryManager.FindByPath(identityDir, true);
            if (folder == null || folder.Id == item.Id)
            {
                return noImage;
            }

            var poster = folder.GetImageInfo(ImageType.Primary, 0)?.Path;
            if (string.IsNullOrEmpty(poster) || !File.Exists(poster))
            {
                return noImage;
            }

            var loaded = await LoadImageFile(poster, cancellationToken).ConfigureAwait(false);
            if (loaded.HasImage)
            {
                _logger.LogDebug("Using the book folder's poster {Poster} for {Path}", poster, item.Path);
            }

            return loaded;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not read the poster of {Path}", identityDir);
            return noImage;
        }
    }

    /// <summary>
    /// True when the folder stands for a single book: it holds this book's audio
    /// tracks (directly or in disc subfolders), or it holds no more than one
    /// self-contained book file. A shelf of PDFs or comics is not one book, and
    /// giving it a looked-up cover would be a guess.
    /// </summary>
    internal static bool RepresentsOneBook(string dir)
    {
        try
        {
            if (Directory.EnumerateFiles(dir).Any(f => AudioExtensions.Contains(Path.GetExtension(f))))
            {
                return true;
            }

            var hasDiscAudio = Directory.EnumerateDirectories(dir)
                .Where(sub => BookIdentity.IsDiscFolderName(Path.GetFileName(sub)))
                .Any(sub => Directory.EnumerateFiles(sub)
                    .Any(f => AudioExtensions.Contains(Path.GetExtension(f))));

            if (hasDiscAudio)
            {
                return true;
            }

            return Directory.EnumerateFiles(dir)
                .Count(f => IsSelfContainedBookFile(Path.GetExtension(f))) <= 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// True for file types that are a whole book on their own.
    /// </summary>
    private static bool IsSelfContainedBookFile(string extension)
        => ComicExtensions.Contains(extension)
            || MobiExtensions.Contains(extension)
            || string.Equals(extension, ".pdf", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".epub", StringComparison.OrdinalIgnoreCase);

    private bool TryGetCachedCover(string identityDir, CoverAttempt attempt, out DynamicImageResponse response)
    {
        lock (_folderCoverLock)
        {
            if (_folderCovers.TryGetValue(identityDir, out var entry))
            {
                attempt.Restore(entry.Outcome);
                response = entry.Data == null
                    ? new DynamicImageResponse { HasImage = false }
                    : new DynamicImageResponse
                    {
                        HasImage = true,
                        Stream = new MemoryStream(entry.Data, writable: false),
                        Format = entry.Format
                    };
                return true;
            }
        }

        response = new DynamicImageResponse { HasImage = false };
        return false;
    }

    /// <summary>
    /// Caches the folder's outcome and returns an equivalent response. The caller's
    /// stream is consumed, so a fresh one is handed back.
    /// </summary>
    private DynamicImageResponse StoreCover(string identityDir, DynamicImageResponse result, CoverAttempt attempt)
    {
        byte[]? data = null;

        if (result.HasImage && result.Stream is MemoryStream ms && ms.Length <= MaxCachedCoverBytes)
        {
            data = ms.ToArray();
            ms.Dispose();
            result = new DynamicImageResponse
            {
                HasImage = true,
                Stream = new MemoryStream(data, writable: false),
                Format = result.Format
            };
        }
        else if (result.HasImage)
        {
            // Too big (or not a MemoryStream) to cache — hand it straight through.
            return result;
        }

        lock (_folderCoverLock)
        {
            if (_folderCovers.TryAdd(identityDir, new CachedCover(data, result.Format, attempt.Snapshot())))
            {
                _folderCoverOrder.Enqueue(identityDir);
                while (_folderCoverOrder.Count > MaxCachedFolders)
                {
                    _folderCovers.Remove(_folderCoverOrder.Dequeue());
                }
            }
        }

        return result;
    }

    private readonly record struct CachedCover(byte[]? Data, ImageFormat Format, CoverAttempt Outcome);

    private async Task<DynamicImageResponse> GetEpubCover(string path, CancellationToken cancellationToken)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);

            var imageEntries = zip.Entries
                .Where(e => !string.IsNullOrEmpty(e.Name) && IsImageFile(e.Name))
                .ToList();

            if (imageEntries.Count == 0)
            {
                return new DynamicImageResponse { HasImage = false };
            }

            // Strategy 1: file explicitly named "cover", "portada", etc.
            var coverByName = imageEntries
                .Where(e => CoverFileNames.Contains(Path.GetFileNameWithoutExtension(e.Name)))
                .OrderByDescending(e => e.Length)
                .FirstOrDefault();

            if (coverByName != null)
            {
                _logger.LogDebug("EPUB cover by name: {Entry} in {Path}", coverByName.FullName, path);
                return await ExtractZipEntry(coverByName, cancellationToken).ConfigureAwait(false);
            }

            // Strategy 2: "cover" anywhere in the path (e.g. OEBPS/Images/cover-image.jpg)
            var coverInPath = imageEntries
                .Where(e => e.FullName.Contains("cover", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(e => e.Length)
                .FirstOrDefault();

            if (coverInPath != null)
            {
                _logger.LogDebug("EPUB cover by path: {Entry} in {Path}", coverInPath.FullName, path);
                return await ExtractZipEntry(coverInPath, cancellationToken).ConfigureAwait(false);
            }

            // Strategy 3: largest image (>5 KB to skip icons/logos)
            var largest = imageEntries
                .Where(e => e.Length > 5_000)
                .OrderByDescending(e => e.Length)
                .FirstOrDefault();

            if (largest != null)
            {
                _logger.LogDebug("EPUB cover by size ({Size} bytes): {Entry} in {Path}", largest.Length, largest.FullName, path);
                return await ExtractZipEntry(largest, cancellationToken).ConfigureAwait(false);
            }

            return new DynamicImageResponse { HasImage = false };
        }
        catch (InvalidDataException)
        {
            _logger.LogWarning("Corrupt or unreadable EPUB archive: {Path}", path);
            return new DynamicImageResponse { HasImage = false };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to extract EPUB cover for {Path}", path);
            return new DynamicImageResponse { HasImage = false };
        }
    }

    private static async Task<DynamicImageResponse> ExtractZipEntry(ZipArchiveEntry entry, CancellationToken cancellationToken)
    {
        var ms = new MemoryStream();
        using (var stream = entry.Open())
        {
            await stream.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
        }

        ms.Position = 0;

        if (ms.Length == 0)
        {
            ms.Dispose();
            return new DynamicImageResponse { HasImage = false };
        }

        var response = new DynamicImageResponse
        {
            HasImage = true,
            Stream = ms
        };

        var ext = Path.GetExtension(entry.Name).ToLowerInvariant();
        var mime = ext switch
        {
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            _ => "image/jpeg"
        };
        response.SetFormatFromMimeType(mime);

        return response;
    }

    private static bool IsImageFile(string fileName)
    {
        return ImageExtensions.Contains(Path.GetExtension(fileName));
    }

    private async Task<DynamicImageResponse> GetComicCover(string path, CancellationToken cancellationToken)
    {
        try
        {
            return await ExtractComicCover(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Corrupt/unreadable archives land here, and so does a missing or
            // unloadable SharpCompress.dll: the assembly only resolves when
            // ExtractComicCover is first invoked, so a load failure surfaces at
            // the call above and degrades to "no image" (+ online fallback)
            // instead of failing the whole scan.
            _logger.LogError(ex, "Failed to extract comic cover for {Path}", path);
            return new DynamicImageResponse { HasImage = false };
        }
    }

    /// <summary>
    /// Extracts a cover from a comic archive (.cbz/.cbr). The archive format is
    /// sniffed from the file content rather than the extension — mislabeled
    /// archives (a RAR named .cbz, a ZIP named .cbr) are common and still open.
    /// An entry explicitly named like a cover wins; otherwise pages are tried in
    /// natural sort order, because the comic convention is that the first page IS
    /// the cover (and natural order keeps page-2 ahead of page-10).
    /// </summary>
    /// <remarks>
    /// SharpCompress forbids mixing access styles: per-entry random access throws on
    /// SOLID archives (each solid entry depends on the ones before it), while the
    /// forward reader (<c>ExtractAllEntries</c>) throws on everything that is NOT
    /// solid/7z. So the chosen candidates are extracted via random access for plain
    /// zip/rar and via a single forward walk for solid archives.
    /// </remarks>
    private async Task<DynamicImageResponse> ExtractComicCover(string path, CancellationToken cancellationToken)
    {
        using var fileStream = File.OpenRead(path);
        using var archive = ArchiveFactory.OpenArchive(fileStream);

        // Single bounded pass over the entry headers: a crafted archive can carry
        // hundreds of thousands of image-named entries, so never materialize or
        // fully sort them. Two small insertion-sorted lists (each capped at
        // MaxComicCandidateAttempts) capture the only entries that can matter:
        // the biggest cover-named ones and the naturally-first pages.
        // Size gate up front so icons/thumbnails (<1 KB) and absurdly large
        // entries never consume a slot; Size <= 0 means the header doesn't report
        // one — let those through, the guards while copying decide.
        var coverNamed = new List<IArchiveEntry>();
        var firstPages = new List<IArchiveEntry>();

        foreach (var entry in archive.Entries)
        {
            if (entry.IsDirectory
                || string.IsNullOrEmpty(entry.Key)
                || (entry.Size > 0 && (entry.Size < 1_000 || entry.Size > MaxComicEntryBytes))
                || !ComicImageExtensions.Contains(Path.GetExtension(GetArchiveEntryFileName(entry.Key)))
                || IsJunkComicEntry(entry.Key))
            {
                continue;
            }

            if (CoverFileNames.Contains(Path.GetFileNameWithoutExtension(GetArchiveEntryFileName(entry.Key))))
            {
                InsertBounded(coverNamed, entry, static (x, y) => y.Size.CompareTo(x.Size), MaxComicCandidateAttempts);
            }

            InsertBounded(firstPages, entry, static (x, y) => NaturalStringComparer.Instance.Compare(x.Key, y.Key), MaxComicCandidateAttempts);
        }

        // Candidate priority: explicit cover entries (largest first) ahead of pages
        // in natural sort order. Distinct() collapses the overlap — both lists hold
        // the same entry instances, so reference equality suffices.
        var candidates = coverNamed
            .Concat(firstPages)
            .Distinct()
            .Take(MaxComicCandidateAttempts)
            .ToList();

        if (candidates.Count == 0)
        {
            return new DynamicImageResponse { HasImage = false };
        }

        var data = archive.IsSolid || archive.Type == SharpCompress.Common.ArchiveType.SevenZip
            ? await ExtractBestCandidateForward(archive, candidates, cancellationToken).ConfigureAwait(false)
            : await ExtractBestCandidateRandomAccess(candidates, cancellationToken).ConfigureAwait(false);

        if (data == null)
        {
            return new DynamicImageResponse { HasImage = false };
        }

        var (format, offset) = DetectImageFormat(data);

        _logger.LogDebug("Comic cover ({Size} bytes) in {Path}", data.Length, path);

        var imageStream = offset > 0
            ? new MemoryStream(data, offset, data.Length - offset)
            : new MemoryStream(data);

        return new DynamicImageResponse
        {
            HasImage = true,
            Stream = imageStream,
            Format = format!.Value
        };
    }

    /// <summary>
    /// Extracts the first candidate that verifies as a real image, opening only the
    /// chosen entries. Valid for non-solid archives (zip, plain rar).
    /// </summary>
    private static async Task<byte[]?> ExtractBestCandidateRandomAccess(
        List<IArchiveEntry> candidates,
        CancellationToken cancellationToken)
    {
        foreach (var entry in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var data = await ReadEntryBytes(entry.OpenEntryStream(), cancellationToken).ConfigureAwait(false);
            if (IsPlausibleCoverImage(data))
            {
                return data;
            }
        }

        return null;
    }

    /// <summary>
    /// Walks a solid archive forward once (random access into a solid stream is not
    /// supported), decodes candidates as they appear physically, keeps the
    /// best-priority one that verifies as a real image, and stops early when the top
    /// candidate succeeds.
    /// </summary>
    private static async Task<byte[]?> ExtractBestCandidateForward(
        IArchive archive,
        List<IArchiveEntry> candidates,
        CancellationToken cancellationToken)
    {
        // TryAdd, not ToDictionary: archives may legally contain duplicate entry
        // names — the first (best-priority) occurrence wins. Rank = candidate
        // priority; lower is better.
        var rankByKey = new Dictionary<string, int>(StringComparer.Ordinal);
        var nextRank = 0;
        foreach (var candidate in candidates)
        {
            rankByKey.TryAdd(candidate.Key!, nextRank++);
        }

        byte[]? bestData = null;
        var bestRank = int.MaxValue;

        using var reader = archive.ExtractAllEntries();
        while (bestRank > 0 && reader.MoveToNextEntry())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var entry = reader.Entry;
            if (entry.IsDirectory
                || string.IsNullOrEmpty(entry.Key)
                || !rankByKey.TryGetValue(entry.Key, out var rank)
                || rank >= bestRank)
            {
                continue;
            }

            var data = await ReadEntryBytes(reader.OpenEntryStream(), cancellationToken).ConfigureAwait(false);
            if (IsPlausibleCoverImage(data))
            {
                bestData = data;
                bestRank = rank;
            }
        }

        return bestData;
    }

    private static async Task<byte[]> ReadEntryBytes(Stream entryStream, CancellationToken cancellationToken)
    {
        await using (entryStream.ConfigureAwait(false))
        {
            using var ms = new MemoryStream();
            var buffer = new byte[81_920];
            int read;
            while ((read = await entryStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                ms.Write(buffer, 0, read);
                if (ms.Length > MaxComicEntryBytes)
                {
                    // Decompression bomb: the header lied about the size. Bail out;
                    // the empty result fails the plausibility check and the entry
                    // is skipped.
                    return [];
                }
            }

            return ms.ToArray();
        }
    }

    /// <summary>
    /// A candidate only counts as a cover when it is big enough to be a page and its
    /// bytes actually look like an image — an image-named entry with garbage content
    /// must not ship as a cover.
    /// </summary>
    private static bool IsPlausibleCoverImage(byte[] data)
        => data.Length >= 1_000 && DetectImageFormat(data).Format != null;

    /// <summary>
    /// Inserts <paramref name="item"/> into a list kept sorted by
    /// <paramref name="comparison"/> and capped at <paramref name="maxCount"/>
    /// elements — the bounded top-K selection that lets one pass over an archive's
    /// headers replace sorting every entry. Stable: ties keep encounter order.
    /// </summary>
    private static void InsertBounded<T>(List<T> list, T item, Comparison<T> comparison, int maxCount)
    {
        var index = 0;
        while (index < list.Count && comparison(list[index], item) <= 0)
        {
            index++;
        }

        if (index >= maxCount)
        {
            return;
        }

        list.Insert(index, item);
        if (list.Count > maxCount)
        {
            list.RemoveAt(list.Count - 1);
        }
    }

    /// <summary>
    /// Returns the file-name part of an archive entry key, tolerating both
    /// separator styles — RAR entries commonly use backslashes, which
    /// <see cref="Path.GetFileName(string)"/> does not treat as separators on Unix.
    /// </summary>
    private static string GetArchiveEntryFileName(string key)
    {
        var idx = key.LastIndexOfAny(['/', '\\']);
        return idx >= 0 ? key[(idx + 1)..] : key;
    }

    /// <summary>
    /// Filters archive junk that must never be picked as a cover: macOS resource
    /// forks (__MACOSX) and hidden dot-files (e.g. ._page-1.jpg AppleDouble stubs).
    /// </summary>
    private static bool IsJunkComicEntry(string key)
    {
        return key.Contains("__MACOSX", StringComparison.OrdinalIgnoreCase)
            || GetArchiveEntryFileName(key).StartsWith('.');
    }

    [ExcludeFromCodeCoverage] // Requires PDFium native library — tested via integration/E2E
    private async Task<DynamicImageResponse> GetPdfCover(string path, CancellationToken cancellationToken)
    {
        var noImage = new DynamicImageResponse { HasImage = false };

        if (!IsPdfRenderingAvailable())
        {
            return noImage;
        }

        var config = Plugin.Instance?.Configuration;
        var dpi = config?.Dpi ?? 150;
        var timeoutSec = config?.TimeoutSeconds ?? 30;
        // Clamp to SkiaSharp's valid JPEG quality range (1-100); default 85.
        var jpegQuality = Math.Clamp(config?.JpegQuality ?? 85, 1, 100);

        try
        {
            // Run synchronous PDFium rendering on a thread-pool thread.
            // Task.Run's token only prevents scheduling — it cannot interrupt
            // rendering once it's running. Race against a standalone Task.Delay
            // for a real timeout boundary. The delay is NOT linked to
            // cancellationToken so that caller cancellation propagates correctly
            // through renderTask (via OCE) instead of being misclassified as a
            // timeout.
            //
            // We render to an SKBitmap and encode the JPEG ourselves rather than
            // calling Conversion.SaveJpeg, because PDFtoImage's RenderOptions has no
            // quality knob and SaveJpeg has no quality overload — encoding via
            // SKBitmap.Encode is the only way to honor the configured JpegQuality.
            var renderTask = Task.Run(
                () =>
                {
                    using var pdfStream = File.OpenRead(path);
                    var options = new RenderOptions { Dpi = dpi };
                    using var bitmap = Conversion.ToImage(pdfStream, page: new Index(0), options: options);
                    var output = new MemoryStream();
                    try
                    {
                        // Encode returns false (writing nothing) on encoder failure;
                        // leave the stream empty so the Length == 0 check below maps it
                        // to "no image" rather than emitting a truncated cover.
                        bitmap.Encode(output, SKEncodedImageFormat.Jpeg, jpegQuality);
                    }
                    catch
                    {
                        output.Dispose();
                        throw;
                    }

                    return output;
                },
                cancellationToken);

            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(timeoutSec));
            var completed = await Task.WhenAny(renderTask, timeoutTask).ConfigureAwait(false);

            if (completed == renderTask)
            {
                // Render finished — await to propagate OCE or other exceptions.
                var ms = await renderTask.ConfigureAwait(false);

                if (ms.Length == 0)
                {
                    ms.Dispose();
                    return noImage;
                }

                ms.Position = 0;

                _logger.LogDebug("Rendered PDF cover ({Size} bytes, {Dpi} DPI) from {Path}", ms.Length, dpi, path);

                return new DynamicImageResponse
                {
                    HasImage = true,
                    Stream = ms,
                    Format = ImageFormat.Jpg
                };
            }

            // Timeout won the race. Distinguish caller cancellation from real timeout.
            cancellationToken.ThrowIfCancellationRequested();

            _logger.LogWarning("PDF rendering timed out after {Timeout}s for {Path}", timeoutSec, path);

            // Observe the orphaned render task to prevent unobserved exceptions
            // and dispose the MemoryStream if it eventually completes.
            _ = renderTask.ContinueWith(
                static t =>
                {
                    if (t.IsCompletedSuccessfully) t.Result.Dispose();
                    _ = t.Exception;
                },
                TaskScheduler.Default);

            return noImage;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to render PDF cover for {Path}", path);
            return noImage;
        }
    }

    /// <summary>
    /// Reports whether PDF cover rendering is available, by checking that the bundled
    /// pdfium native library can be loaded. The result is computed once and cached for
    /// the lifetime of the singleton; the computation is thread-safe.
    /// </summary>
    /// <remarks>
    /// This deliberately probes the native library directly (via the injected probe,
    /// which defaults to <see cref="PdfiumNativeLibrary.TryLoad"/>) and does NOT call any
    /// PDFtoImage rendering API — so when pdfium cannot load, no finalizable
    /// <c>PdfLibrary</c> is ever constructed. See <see cref="PdfiumNativeLibrary"/> for
    /// why touching PDFtoImage before the native loads would crash the host process.
    /// </remarks>
    internal bool IsPdfRenderingAvailable()
    {
        var state = _pdfRenderingState;
        if (state != PdfStateUnknown)
        {
            return state == PdfStateAvailable;
        }

        lock (_pdfProbeLock)
        {
            if (_pdfRenderingState != PdfStateUnknown)
            {
                return _pdfRenderingState == PdfStateAvailable;
            }

            bool available;
            try
            {
                available = _pdfiumNativeProbe();
            }
            catch (Exception ex)
            {
                // TryLoad itself should never throw, but guard defensively so a probe
                // failure can never escalate the way the original render-probe did.
                available = false;
                _logger.LogWarning(ex, "pdfium native availability probe failed — PDF cover extraction disabled");
            }

            if (available)
            {
                _logger.LogInformation("pdfium native library loaded — PDF cover extraction enabled");
            }
            else
            {
                _logger.LogWarning("pdfium native library not available — PDF cover extraction disabled");
            }

            _pdfRenderingState = available ? PdfStateAvailable : PdfStateUnavailable;
            return available;
        }
    }

    internal static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best-effort cleanup
        }
    }

    internal static void CleanupTemp(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup
        }
    }

    [ExcludeFromCodeCoverage] // Requires ffmpeg process — tested via integration/E2E
    private async Task<DynamicImageResponse> GetAudioCover(string path, CancellationToken cancellationToken)
    {
        var noImage = new DynamicImageResponse { HasImage = false };
        var ffmpegPath = GetFfmpegPath();

        if (ffmpegPath == null)
        {
            return noImage;
        }

        var config = Plugin.Instance?.Configuration;
        var timeoutSec = config?.TimeoutSeconds ?? 30;

        // Use .jpg extension so ffmpeg can determine the output muxer (image2).
        // The actual format is detected from magic bytes regardless of extension.
        var tempFile = Path.Combine(Path.GetTempPath(), $"jf-audio-{Guid.NewGuid():N}.jpg");

        try
        {
            // Raw-copy the embedded art stream without re-encoding.
            // This bypasses codec tag validation, which is exactly what fails
            // in Jellyfin's built-in Image Extractor when ID3 tags declare PNG
            // but the actual data is JPEG (or vice versa).
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                RedirectStandardOutput = false,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            process.StartInfo.ArgumentList.Add("-v");
            process.StartInfo.ArgumentList.Add("error");
            process.StartInfo.ArgumentList.Add("-y");
            process.StartInfo.ArgumentList.Add("-i");
            process.StartInfo.ArgumentList.Add(path);
            process.StartInfo.ArgumentList.Add("-an");
            process.StartInfo.ArgumentList.Add("-vcodec");
            process.StartInfo.ArgumentList.Add("copy");
            process.StartInfo.ArgumentList.Add(tempFile);

            process.Start();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                _logger.LogWarning("ffmpeg timed out after {Timeout}s for {Path}", timeoutSec, path);
                return noImage;
            }

            if (!File.Exists(tempFile))
            {
                return noImage;
            }

            var bytes = await File.ReadAllBytesAsync(tempFile, cancellationToken).ConfigureAwait(false);
            CleanupTemp(tempFile);

            if (bytes.Length < 1000)
            {
                return noImage;
            }

            var (format, dataOffset) = DetectImageFormat(bytes);
            if (format == null)
            {
                _logger.LogDebug("Extracted embedded art but unrecognised image format for {Path}", path);
                return noImage;
            }

            _logger.LogDebug("Extracted {Format} cover ({Size} bytes, offset {Offset}) from {Path}", format, bytes.Length, dataOffset, path);

            // Strip any leading padding bytes before the actual image header
            var imageStream = dataOffset > 0
                ? new MemoryStream(bytes, dataOffset, bytes.Length - dataOffset)
                : new MemoryStream(bytes);

            return new DynamicImageResponse
            {
                HasImage = true,
                Stream = imageStream,
                Format = format.Value
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to extract audio cover for {Path}", path);
            CleanupTemp(tempFile);
            return noImage;
        }
    }

    /// <summary>
    /// Checks for a sidecar image file next to the audio file — either with
    /// the same base name (e.g. audiobook.jpg next to audiobook.m4b) or with
    /// common cover names (cover.jpg, folder.jpg, front.jpg) in the same directory.
    /// </summary>
    private async Task<DynamicImageResponse> GetSidecarImage(string audioPath, CancellationToken cancellationToken)
    {
        var noImage = new DynamicImageResponse { HasImage = false };
        var dir = Path.GetDirectoryName(audioPath);

        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            return noImage;
        }

        var baseName = Path.GetFileNameWithoutExtension(audioPath);
        string[] imageExtensions = [".jpg", ".jpeg", ".png", ".webp"];
        string[] coverNames = [baseName, "cover", "folder", "front", "poster", "thumb"];

        try
        {
            foreach (var name in coverNames)
            {
                foreach (var ext in imageExtensions)
                {
                    var candidate = Path.Combine(dir, name + ext);
                    if (File.Exists(candidate))
                    {
                        var bytes = await File.ReadAllBytesAsync(candidate, cancellationToken).ConfigureAwait(false);
                        if (bytes.Length < 1000 || GeneratedCover.IsGenerated(bytes))
                        {
                            continue;
                        }

                        var (format, offset) = DetectImageFormat(bytes);
                        if (format == null)
                        {
                            continue;
                        }

                        _logger.LogDebug("Found sidecar cover {File} for {Path}", candidate, audioPath);

                        var imageStream = offset > 0
                            ? new MemoryStream(bytes, offset, bytes.Length - offset)
                            : new MemoryStream(bytes);

                        return new DynamicImageResponse
                        {
                            HasImage = true,
                            Stream = imageStream,
                            Format = format.Value
                        };
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Error checking sidecar images for {Path}", audioPath);
        }

        return noImage;
    }

    /// <summary>
    /// The cover for a book folder: an image file sitting in it, else embedded art
    /// from its first audio file (looking inside disc subfolders when the book is a
    /// multi-disc rip and the folder itself holds no audio).
    /// </summary>
    private async Task<DynamicImageResponse> GetFolderAudioCover(string dirPath, CancellationToken cancellationToken)
    {
        try
        {
            var image = await GetFolderImageFile(dirPath, cancellationToken).ConfigureAwait(false);
            if (image.HasImage)
            {
                return image;
            }

            foreach (var audioFile in EnumerateBookAudioFiles(dirPath))
            {
                var result = await GetAudioCover(audioFile, cancellationToken).ConfigureAwait(false);
                if (result.HasImage)
                {
                    return result;
                }

                result = await GetSidecarImage(audioFile, cancellationToken).ConfigureAwait(false);
                if (result.HasImage)
                {
                    return result;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to scan folder for a cover: {Path}", dirPath);
        }

        return new DynamicImageResponse { HasImage = false };
    }

    /// <summary>
    /// The first audio file of the book: the folder's own, else the first disc
    /// subfolder's. Only one candidate per location — probing every track of a
    /// 100-file rip would cost a hundred ffmpeg runs for one cover.
    /// </summary>
    private static IEnumerable<string> EnumerateBookAudioFiles(string dirPath)
    {
        var own = FirstAudioFile(dirPath);
        if (own != null)
        {
            yield return own;
            yield break;
        }

        var firstDisc = Directory.EnumerateDirectories(dirPath)
            .Where(sub => BookIdentity.IsDiscFolderName(Path.GetFileName(sub)))
            .OrderBy(sub => sub, NaturalStringComparer.Instance)
            .FirstOrDefault();

        var discAudio = firstDisc == null ? null : FirstAudioFile(firstDisc);
        if (discAudio != null)
        {
            yield return discAudio;
        }
    }

    private static string? FirstAudioFile(string dirPath)
        => Directory.EnumerateFiles(dirPath)
            .Where(f => AudioExtensions.Contains(Path.GetExtension(f)))
            .OrderBy(f => f, NaturalStringComparer.Instance)
            .FirstOrDefault();

    /// <summary>
    /// Finds a cover image file in the folder. An exact "cover.jpg" wins; then any
    /// image whose name contains a cover word ("CoverArt.jpg", "Make Time-Cover.jpg");
    /// then, when the folder holds exactly one image, that image — a lone picture
    /// beside a book's audio is its cover.
    /// </summary>
    private async Task<DynamicImageResponse> GetFolderImageFile(string dirPath, CancellationToken cancellationToken)
    {
        var noImage = new DynamicImageResponse { HasImage = false };

        var images = Directory.EnumerateFiles(dirPath)
            .Where(f => ImageExtensions.Contains(Path.GetExtension(f)))
            .OrderBy(f => f, NaturalStringComparer.Instance)
            .ToList();

        if (images.Count == 0)
        {
            return noImage;
        }

        var exact = images.FirstOrDefault(
            f => CoverFileNames.Contains(Path.GetFileNameWithoutExtension(f)));

        var byHint = images.FirstOrDefault(
            f => CoverNameHints.Any(h => Path.GetFileNameWithoutExtension(f)
                .Contains(h, StringComparison.OrdinalIgnoreCase)));

        // An image named after the book: "Cabo Trafalgar.jpg" in
        // "[Audiolibro mp3]  Cabo Trafalgar- Arturo Pérez-Reverte 2004".
        var folderName = Path.GetFileName(dirPath);
        var bookNames = new[] { folderName, OnlineCoverFetcher.ParseName(folderName).Title }
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => BookNameParser.Fold(n.Trim()))
            .ToHashSet(StringComparer.Ordinal);
        var byTitle = images.FirstOrDefault(
            f => bookNames.Contains(BookNameParser.Fold(Path.GetFileNameWithoutExtension(f).Trim())));

        var lone = images.Count == 1 ? images[0] : null;

        foreach (var candidate in new[] { exact, byHint, byTitle, lone })
        {
            if (candidate == null)
            {
                continue;
            }

            var loaded = await LoadImageFile(candidate, cancellationToken).ConfigureAwait(false);
            if (loaded.HasImage)
            {
                _logger.LogDebug("Found folder cover image {File}", candidate);
                return loaded;
            }
        }

        return noImage;
    }

    /// <summary>
    /// Reads an image file, rejecting placeholders and anything whose magic bytes
    /// do not match a format Jellyfin can display.
    /// </summary>
    private static async Task<DynamicImageResponse> LoadImageFile(string path, CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);

        // A drawn cover that Jellyfin saved beside the media is not a real one.
        if (bytes.Length < 1000 || GeneratedCover.IsGenerated(bytes))
        {
            return new DynamicImageResponse { HasImage = false };
        }

        var (format, offset) = DetectImageFormat(bytes);
        if (format == null)
        {
            return new DynamicImageResponse { HasImage = false };
        }

        return new DynamicImageResponse
        {
            HasImage = true,
            Stream = offset > 0 ? new MemoryStream(bytes, offset, bytes.Length - offset) : new MemoryStream(bytes),
            Format = format.Value
        };
    }

    /// <summary>
    /// Reads the cover embedded in a MOBI / AZW e-book. Jellyfin's built-in providers
    /// do not open these, so without this the file has no local cover at all.
    /// </summary>
    private async Task<DynamicImageResponse> GetMobiCover(string path, CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await Task.Run(
                () =>
                {
                    using var stream = File.OpenRead(path);
                    return MobiCoverExtractor.TryExtractCover(stream, cancellationToken);
                },
                cancellationToken).ConfigureAwait(false);

            if (bytes == null)
            {
                _logger.LogDebug("No embedded cover in MOBI {Path}", path);
                return new DynamicImageResponse { HasImage = false };
            }

            var (format, offset) = DetectImageFormat(bytes);
            if (format == null)
            {
                return new DynamicImageResponse { HasImage = false };
            }

            _logger.LogDebug("Extracted MOBI cover ({Size} bytes) from {Path}", bytes.Length, path);

            return new DynamicImageResponse
            {
                HasImage = true,
                Stream = offset > 0 ? new MemoryStream(bytes, offset, bytes.Length - offset) : new MemoryStream(bytes),
                Format = format.Value
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to read MOBI cover from {Path}", path);
            return new DynamicImageResponse { HasImage = false };
        }
    }

    /// <summary>
    /// Last resort: ask an online book catalogue for the cover.
    /// </summary>
    /// <param name="item">The item being given a cover.</param>
    /// <param name="identityDir">
    /// The book folder to take the title from, or null to use the item's own name.
    /// </param>
    /// <param name="attempt">Where the outcome is written.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    private async Task<DynamicImageResponse> GetOnlineCover(
        BaseItem item, string? identityDir, CoverAttempt attempt, CancellationToken cancellationToken)
    {
        var noImage = new DynamicImageResponse { HasImage = false };

        BookQuery query;
        try
        {
            query = BuildQuery(item, identityDir, attempt);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not work out the title of '{Name}'", item.Name);
            attempt.Reason = CoverReason.UnsearchableTitle;
            return noImage;
        }

        if (!IsBookLibrary(item))
        {
            attempt.Reason = CoverReason.NotBookLibrary;
            return noImage;
        }

        if (Configuration?.EnableOnlineCoverFetch != true)
        {
            attempt.Reason = CoverReason.OnlineDisabled;
            return noImage;
        }

        if (!OnlineCoverFetcher.IsSearchableTitle(query.Title) && query.Isbns.Count == 0)
        {
            _logger.LogDebug("Skipping online lookup, '{Title}' is not a usable title", query.Title);
            attempt.Reason = CoverReason.UnsearchableTitle;
            return noImage;
        }

        try
        {
            _logger.LogDebug("Attempting online cover fetch for: '{Title}' by '{Author}'", query.Title, query.Author ?? "(unknown)");

            var lookup = await _onlineFetcher.LookupAsync(query, cancellationToken).ConfigureAwait(false);

            // Fallback: retry with OriginalTitle (often the English title for non-English libraries)
            if (lookup.Cover == null && !lookup.NetworkError)
            {
                var byOriginal = await _onlineFetcher.LookupByOriginalTitleAsync(item, query.Author, cancellationToken).ConfigureAwait(false);
                if (byOriginal != null)
                {
                    lookup = byOriginal with { Detail = byOriginal.Detail ?? lookup.Detail };
                }
            }

            if (lookup.Cover == null)
            {
                attempt.Reason = lookup.NetworkError ? CoverReason.NetworkError : CoverReason.NoOnlineMatch;
                attempt.Detail = lookup.Detail;
                _logger.LogDebug("No online cover found for '{Title}': {Detail}", query.Title, lookup.Detail ?? "no match");
                return noImage;
            }

            attempt.Reason = CoverReason.FoundOnline;
            attempt.Source = lookup.Source;
            return new DynamicImageResponse
            {
                HasImage = true,
                Stream = lookup.Cover.Value.Stream,
                Format = lookup.Cover.Value.Format
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Online cover fetch failed for '{Name}'", item.Name);
            attempt.Reason = CoverReason.NetworkError;
            attempt.Detail = ex.Message;
            return noImage;
        }
    }

    /// <summary>
    /// What to search for. The book's own metadata wins over its file name: an
    /// EPUB's OPF title, author and ISBN, then the authors Jellyfin holds, then
    /// whatever the name and tags say.
    /// </summary>
    internal BookQuery BuildQuery(BaseItem item, string? identityDir, CoverAttempt? attempt = null)
    {
        var query = OnlineCoverFetcher.ParseBookQuery(item, identityDir == null ? null : Path.GetFileName(identityDir));
        string? displayTitle = null;

        if (identityDir == null
            && item.Path is { } path
            && string.Equals(Path.GetExtension(path), ".epub", StringComparison.OrdinalIgnoreCase)
            && EpubOpf.Read(path) is { } hints)
        {
            var opfTitle = hints.Title == null ? null : BookNameParser.OcrTag.Replace(hints.Title, string.Empty).Trim();
            if (IsUsableOpfTitle(opfTitle))
            {
                displayTitle = opfTitle;
                query = query with
                {
                    Title = OnlineCoverFetcher.CleanText(opfTitle!),
                    Author = hints.Author ?? query.Author
                };
            }
            else if (query.Author == null && hints.Author != null)
            {
                query = query with { Author = hints.Author };
            }

            if (hints.Isbns.Count > 0)
            {
                query = query with { Isbns = query.Isbns.Concat(hints.Isbns).Distinct(StringComparer.Ordinal).ToList() };
            }
        }

        if (query.Author == null && item is Book)
        {
            query = query with { Author = AuthorFromPeople(item) };
        }

        if (attempt != null)
        {
            attempt.Title = query.Title;
            attempt.DisplayTitle = displayTitle;
            attempt.Author = query.Author;
        }

        return query;
    }

    private static bool IsUsableOpfTitle(string? title)
        => OnlineCoverFetcher.IsSearchableTitle(title)
            && !title!.Contains(" -- ", StringComparison.Ordinal)
            && !string.Equals(title, "Unknown", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(title, "Untitled", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The first author Jellyfin holds for the item (from its metadata providers).
    /// </summary>
    private string? AuthorFromPeople(BaseItem item)
    {
        if (_libraryManager == null)
        {
            return null;
        }

        try
        {
            return _libraryManager
                .GetPeople(item)
                .FirstOrDefault(p => p.Type == PersonKind.Author && !string.IsNullOrWhiteSpace(p.Name))
                ?.Name;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not read the authors of '{Name}'", item.Name);
            return null;
        }
    }

    /// <summary>
    /// Draws a plain title and author cover, when the setting is on and every real
    /// source has definitely missed. Never after a network error (the next run may
    /// find the real cover), never outside book libraries except for a music track
    /// that belongs to no album, never over a real cover, and never again for an
    /// item that already carries a drawn cover.
    /// </summary>
    private DynamicImageResponse MaybeGenerate(BaseItem item, CoverAttempt attempt)
    {
        var noImage = new DynamicImageResponse { HasImage = false };
        var config = Configuration;
        if (config?.EnableGeneratedCovers != true
            || attempt.Reason is not (CoverReason.NoImageInFile or CoverReason.NoOnlineMatch or CoverReason.OnlineDisabled))
        {
            return noImage;
        }

        var title = attempt.DisplayTitle ?? attempt.Title;
        if (!OnlineCoverFetcher.IsSearchableTitle(title))
        {
            return noImage;
        }

        var eligible = IsBookLike(item) ? IsBookLibrary(item) : attempt.LooseMusic;
        if (!eligible)
        {
            return noImage;
        }

        // Jellyfin keeps an image no provider replaces, so returning nothing leaves
        // the drawn cover in place without rewriting it every night.
        var current = PrimaryImagePath(item);
        if (GeneratedCover.IsGeneratedFile(current))
        {
            attempt.Reason = CoverReason.KeptGenerated;
            return noImage;
        }

        // A real cover is never replaced by a drawn one. Jellyfin asks providers
        // again when images are being replaced, and a cover fetched earlier by
        // another provider is not one ours can find.
        if (!string.IsNullOrEmpty(current))
        {
            return noImage;
        }

        var bytes = RenderGenerated(title!, attempt.Author, config.JpegQuality);
        if (bytes == null)
        {
            attempt.Reason = CoverReason.NoFont;
            if (!_noFontWarned)
            {
                _noFontWarned = true;
                _logger.LogWarning("Cannot draw a cover: no font on this server can draw '{Title}'", title);
            }

            return noImage;
        }

        attempt.Reason = CoverReason.Generated;
        return new DynamicImageResponse
        {
            HasImage = true,
            Stream = new MemoryStream(bytes, writable: false),
            Format = ImageFormat.Jpg
        };
    }

    /// <summary>
    /// Renders a drawn cover, reusing the last one: every track of a folder
    /// audiobook asks for the same cover.
    /// </summary>
    private byte[]? RenderGenerated(string title, string? author, int jpegQuality)
    {
        var key = $"{title}\n{author}\n{jpegQuality}";
        lock (_generatedLock)
        {
            if (_generatedBytes != null && string.Equals(_generatedKey, key, StringComparison.Ordinal))
            {
                return _generatedBytes;
            }
        }

        var bytes = GeneratedCover.Render(title, author, jpegQuality, GeneratedFontPath);
        if (bytes != null)
        {
            GeneratedRenderCount++;
            lock (_generatedLock)
            {
                _generatedKey = key;
                _generatedBytes = bytes;
            }
        }

        return bytes;
    }

    /// <summary>
    /// A font file for drawn covers instead of the system fonts. Tests only.
    /// </summary>
    internal string? GeneratedFontPath { get; set; }

    /// <summary>
    /// How many covers have been drawn (cache misses only). Tests only.
    /// </summary>
    internal int GeneratedRenderCount { get; private set; }

    private static string? PrimaryImagePath(BaseItem item)
    {
        try
        {
            return item.GetImageInfo(ImageType.Primary, 0)?.Path;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Detects the actual image format from magic bytes, ignoring file
    /// extensions or codec tags which may be incorrect. Scans past any
    /// leading null/padding bytes that raw stream copy may include.
    /// </summary>
    /// <returns>Tuple of (format, byte offset where the image header starts).</returns>
    internal static (ImageFormat? Format, int Offset) DetectImageFormat(byte[] data)
    {
        if (data.Length < 4)
        {
            return (null, 0);
        }

        // Skip leading null bytes (raw stream copy can include padding)
        int offset = 0;
        while (offset < data.Length && offset < 16 && data[offset] == 0x00)
        {
            offset++;
        }

        if (offset + 4 > data.Length)
        {
            return (null, 0);
        }

        // JPEG: FF D8 FF
        if (data[offset] == 0xFF && data[offset + 1] == 0xD8 && data[offset + 2] == 0xFF)
        {
            return (ImageFormat.Jpg, offset);
        }

        // PNG: 89 50 4E 47
        if (data[offset] == 0x89 && data[offset + 1] == 0x50 && data[offset + 2] == 0x4E && data[offset + 3] == 0x47)
        {
            return (ImageFormat.Png, offset);
        }

        // GIF: 47 49 46
        if (data[offset] == 0x47 && data[offset + 1] == 0x49 && data[offset + 2] == 0x46)
        {
            return (ImageFormat.Gif, offset);
        }

        // BMP: "BM" + a known DIB-header size at offset 14 (12 = BITMAPCOREHEADER,
        // 40/52/56 = BITMAPINFOHEADER v1-v3, 64 = OS/2 v2, 108/124 = v4/v5).
        // "BM" alone matches far too much arbitrary data to trust for entries
        // coming out of untrusted archives.
        if (offset + 18 <= data.Length && data[offset] == 0x42 && data[offset + 1] == 0x4D)
        {
            var dibSize = data[offset + 14] | (data[offset + 15] << 8) | (data[offset + 16] << 16) | (data[offset + 17] << 24);
            if (dibSize is 12 or 40 or 52 or 56 or 64 or 108 or 124)
            {
                return (ImageFormat.Bmp, offset);
            }
        }

        // WebP: RIFF....WEBP
        if (offset + 12 < data.Length
            && data[offset] == 0x52 && data[offset + 1] == 0x49 && data[offset + 2] == 0x46 && data[offset + 3] == 0x46
            && data[offset + 8] == 0x57 && data[offset + 9] == 0x45 && data[offset + 10] == 0x42 && data[offset + 11] == 0x50)
        {
            return (ImageFormat.Webp, offset);
        }

        return (null, 0);
    }

    /// <summary>
    /// Locates ffmpeg — checks the Jellyfin bundled path first, then system PATH.
    /// </summary>
    internal string? GetFfmpegPath()
    {
        if (_ffmpegChecked)
        {
            return _ffmpegPath;
        }

        _ffmpegChecked = true;

        const string jellyfinFfmpeg = "/usr/lib/jellyfin-ffmpeg/ffmpeg";
        if (File.Exists(jellyfinFfmpeg))
        {
            _ffmpegPath = jellyfinFfmpeg;
            _logger.LogInformation("Found jellyfin-ffmpeg — audio cover extraction enabled");
            return _ffmpegPath;
        }

        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                ArgumentList = { "-version" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            process.Start();
            if (process.WaitForExit(5000) && process.ExitCode == 0)
            {
                _ffmpegPath = "ffmpeg";
                _logger.LogInformation("Found system ffmpeg — audio cover extraction enabled");
            }
            else
            {
                TryKill(process);
                _logger.LogWarning("ffmpeg probe timed out or returned non-zero exit code. Audio cover extraction disabled");
                _ffmpegPath = null;
            }
        }
        catch (Exception)
        {
            _logger.LogWarning("ffmpeg not found. Audio cover extraction disabled");
            _ffmpegPath = null;
        }

        return _ffmpegPath;
    }
}

/// <summary>
/// The outcome of one cover request as it is being worked out.
/// </summary>
internal sealed class CoverAttempt
{
    /// <summary>Gets or sets how the request ended so far.</summary>
    public CoverReason Reason { get; set; } = CoverReason.NoImageInFile;

    /// <summary>Gets or sets the title searched for.</summary>
    public string? Title { get; set; }

    /// <summary>Gets or sets the title to draw, when it differs from the searched one.</summary>
    public string? DisplayTitle { get; set; }

    /// <summary>Gets or sets the author.</summary>
    public string? Author { get; set; }

    /// <summary>Gets or sets what went wrong, for the log.</summary>
    public string? Detail { get; set; }

    /// <summary>Gets or sets the catalogue the cover came from.</summary>
    public string? Source { get; set; }

    /// <summary>Gets or sets a value indicating whether this is a music file that belongs to no album.</summary>
    public bool LooseMusic { get; set; }

    /// <summary>A copy for the folder cache.</summary>
    public CoverAttempt Snapshot() => (CoverAttempt)MemberwiseClone();

    /// <summary>Takes the outcome cached for the book folder.</summary>
    public void Restore(CoverAttempt cached)
    {
        Reason = cached.Reason;
        Title = cached.Title;
        DisplayTitle = cached.DisplayTitle;
        Author = cached.Author;
        Detail = cached.Detail;
        Source = cached.Source;
    }

    /// <summary>The outcome as the scheduled task reads it.</summary>
    public CoverOutcome ToOutcome() => new(Reason, DisplayTitle ?? Title, Author, Detail, Source);
}
