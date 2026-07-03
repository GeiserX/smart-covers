using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Net;
using Microsoft.Extensions.Logging;
using PDFtoImage;
using SharpCompress.Archives;
using SkiaSharp;

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

    private readonly ILogger<CoverImageProvider> _logger;
    private readonly OnlineCoverFetcher _onlineFetcher;
    private readonly object _pdfProbeLock = new();
    private readonly Func<bool> _pdfiumNativeProbe;
    private volatile int _pdfRenderingState;
    private string? _ffmpegPath;
    private bool _ffmpegChecked;

    /// <summary>
    /// Initializes a new instance of the <see cref="CoverImageProvider"/> class.
    /// </summary>
    public CoverImageProvider(ILogger<CoverImageProvider> logger, OnlineCoverFetcher onlineFetcher)
        : this(logger, onlineFetcher, null)
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
    internal CoverImageProvider(ILogger<CoverImageProvider> logger, OnlineCoverFetcher onlineFetcher, Func<bool>? pdfiumNativeProbe)
    {
        _logger = logger;
        _onlineFetcher = onlineFetcher;
        _pdfiumNativeProbe = pdfiumNativeProbe ?? DefaultPdfiumNativeProbe;
    }

    /// <summary>
    /// The default pdfium availability probe: loads (and pins) the native via the shared
    /// <see cref="PdfiumNativeLibrary"/>. It touches NO PDFtoImage type, so a failed probe
    /// never leaves a finalizable <c>PdfLibrary</c> behind (see that type for the full
    /// finalizer-crash rationale).
    /// </summary>
    private static bool DefaultPdfiumNativeProbe() => PdfiumNativeLibrary.TryLoad(out _);

    /// <inheritdoc />
    public string Name => "SmartCovers";

    /// <inheritdoc />
    public bool Supports(BaseItem item) => item is Book || item is AudioBook || item is Audio || item is MusicAlbum;

    /// <inheritdoc />
    public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
    {
        yield return ImageType.Primary;
    }

    /// <inheritdoc />
    public async Task<DynamicImageResponse> GetImage(BaseItem item, ImageType type, CancellationToken cancellationToken)
    {
        var path = item.Path;

        if (string.IsNullOrEmpty(path))
        {
            return await GetOnlineCover(item, cancellationToken).ConfigureAwait(false);
        }

        DynamicImageResponse result;

        if (Directory.Exists(path))
        {
            result = await GetFolderAudioCover(path, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var ext = Path.GetExtension(path);

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
            else if (AudioExtensions.Contains(ext))
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
        }

        // Final fallback: fetch cover from online sources (books/audiobooks only).
        // Online fetcher queries Open Library and Google Books — irrelevant for music.
        if (!result.HasImage && item is not Audio && item is not MusicAlbum)
        {
            result = await GetOnlineCover(item, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

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
                        if (bytes.Length < 1000)
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
    /// For folder-based audiobooks (multi-file chapters), extracts embedded
    /// art from the first audio file in the directory.
    /// </summary>
    private async Task<DynamicImageResponse> GetFolderAudioCover(string dirPath, CancellationToken cancellationToken)
    {
        try
        {
            // First check for cover/folder images in the directory
            string[] imageExtensions = [".jpg", ".jpeg", ".png", ".webp"];
            string[] coverNames = ["cover", "folder", "front", "poster", "thumb"];

            foreach (var name in coverNames)
            {
                foreach (var ext in imageExtensions)
                {
                    var candidate = Path.Combine(dirPath, name + ext);
                    if (File.Exists(candidate))
                    {
                        var bytes = await File.ReadAllBytesAsync(candidate, cancellationToken).ConfigureAwait(false);
                        if (bytes.Length >= 1000)
                        {
                            var (format, offset) = DetectImageFormat(bytes);
                            if (format != null)
                            {
                                _logger.LogDebug("Found folder cover image {File}", candidate);
                                var imageStream = offset > 0
                                    ? new MemoryStream(bytes, offset, bytes.Length - offset)
                                    : new MemoryStream(bytes);
                                return new DynamicImageResponse { HasImage = true, Stream = imageStream, Format = format.Value };
                            }
                        }
                    }
                }
            }

            // Then try extracting embedded art from the first audio file
            var audioFile = Directory.EnumerateFiles(dirPath)
                .Where(f => AudioExtensions.Contains(Path.GetExtension(f)))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            if (audioFile != null)
            {
                var result = await GetAudioCover(audioFile, cancellationToken).ConfigureAwait(false);
                if (result.HasImage)
                {
                    return result;
                }

                // Final fallback: sidecar image next to the audio file
                return await GetSidecarImage(audioFile, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to scan folder for audio files: {Path}", dirPath);
        }

        return new DynamicImageResponse { HasImage = false };
    }

    private async Task<DynamicImageResponse> GetOnlineCover(BaseItem item, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config?.EnableOnlineCoverFetch != true)
        {
            return new DynamicImageResponse { HasImage = false };
        }

        try
        {
            var (title, author) = OnlineCoverFetcher.ParseBookInfo(item);
            if (string.IsNullOrWhiteSpace(title))
            {
                return new DynamicImageResponse { HasImage = false };
            }

            _logger.LogDebug("Attempting online cover fetch for: '{Title}' by '{Author}'", title, author ?? "(unknown)");

            var cover = await _onlineFetcher.FetchCoverAsync(title, author, cancellationToken).ConfigureAwait(false);

            // Fallback: retry with OriginalTitle (often the English title for non-English libraries)
            cover ??= await _onlineFetcher.FetchCoverByOriginalTitleAsync(item, cancellationToken).ConfigureAwait(false);

            if (cover == null)
            {
                _logger.LogDebug("No online cover found for '{Title}'", title);
                return new DynamicImageResponse { HasImage = false };
            }

            return new DynamicImageResponse
            {
                HasImage = true,
                Stream = cover.Value.Stream,
                Format = cover.Value.Format
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Online cover fetch failed for '{Name}'", item.Name);
            return new DynamicImageResponse { HasImage = false };
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
