using System.Diagnostics;
using System.IO.Compression;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Net;
using Microsoft.Extensions.Logging;
using PDFtoImage;

namespace SmartCovers;

/// <summary>
/// Fallback cover provider for books and audiobooks. Handles PDFs (first page
/// rendered via built-in PDFium), EPUBs (aggressive image search inside the ZIP
/// archive), and audio files (embedded art extraction via ffmpeg raw stream copy).
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

    private readonly ILogger<CoverImageProvider> _logger;
    private readonly OnlineCoverFetcher _onlineFetcher;
    private bool? _pdfRenderingAvailable;
    private string? _ffmpegPath;
    private bool _ffmpegChecked;

    /// <summary>
    /// Initializes a new instance of the <see cref="CoverImageProvider"/> class.
    /// </summary>
    public CoverImageProvider(ILogger<CoverImageProvider> logger, OnlineCoverFetcher onlineFetcher)
    {
        _logger = logger;
        _onlineFetcher = onlineFetcher;
    }

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

        try
        {
            // Run synchronous PDFium rendering on a thread-pool thread.
            // Task.Run's token only prevents scheduling — it cannot interrupt
            // SaveJpeg once it's running. Race against a standalone Task.Delay
            // for a real timeout boundary. The delay is NOT linked to
            // cancellationToken so that caller cancellation propagates correctly
            // through renderTask (via OCE) instead of being misclassified as a
            // timeout.
            var renderTask = Task.Run(
                () =>
                {
                    using var pdfStream = File.OpenRead(path);
                    var output = new MemoryStream();
                    var options = new RenderOptions { Dpi = dpi };
                    Conversion.SaveJpeg(output, pdfStream, page: new Index(0), options: options);
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
    /// Probes whether the bundled PDFium native library loads successfully by
    /// attempting to render a minimal 1-page PDF. Result is cached for the
    /// lifetime of the singleton.
    /// </summary>
    internal bool IsPdfRenderingAvailable()
    {
        if (_pdfRenderingAvailable.HasValue)
        {
            return _pdfRenderingAvailable.Value;
        }

        try
        {
            // Minimal valid PDF (1 blank page) to trigger native pdfium load.
            var minimalPdf = "%PDF-1.0\n1 0 obj<</Pages 2 0 R>>endobj\n2 0 obj<</Kids[3 0 R]/Count 1>>endobj\n3 0 obj<</MediaBox[0 0 1 1]>>endobj\ntrailer<</Root 1 0 R>>"u8;
            using var pdfStream = new MemoryStream(minimalPdf.ToArray());
            using var output = new MemoryStream();
            Conversion.SaveJpeg(output, pdfStream, page: new Index(0));
            _pdfRenderingAvailable = output.Length > 0;
        }
        catch (Exception ex)
        {
            _pdfRenderingAvailable = false;
            _logger.LogWarning(ex, "PDFium native library failed to load — PDF cover extraction disabled");
        }

        if (_pdfRenderingAvailable == true)
        {
            _logger.LogInformation("PDFium loaded — PDF cover extraction enabled");
        }

        return _pdfRenderingAvailable.Value;
    }

    private static void TryKill(Process process)
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

    private static void CleanupTemp(string path)
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
