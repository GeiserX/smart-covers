using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Drawing;
using Microsoft.Extensions.Logging;

namespace SmartCovers;

/// <summary>
/// Fetches book/audiobook covers from online sources (Open Library, Google Books)
/// as a last-resort fallback when local extraction methods fail.
/// </summary>
public class OnlineCoverFetcher
{
    private static readonly HttpClient Http = CreateHttpClient();

    private static readonly Regex FormatTagRegex = new(
        @"\((?:Mp3|M4[ab]|FLAC|OGG|Opus|WAV|WMA|AAC|mp3)[\s\-]*[^)]*\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex LocaleTagRegex = new(
        @"\[(?:Castellano|Español|Latino|English|Narración)[^\]]*\]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TrailingYearRegex = new(
        @"\s*[-–—]\s*\d{4}\s*$",
        RegexOptions.Compiled);

    private static readonly Regex ParenYearRegex = new(
        @"\(\d{4}\)",
        RegexOptions.Compiled);

    private static readonly Regex ParenYearAuthorRegex = new(
        @"\((\d{4}),\s*([^)]+)\)",
        RegexOptions.Compiled);

    private static readonly Regex SagaRegex = new(
        @"\s*[-–—]\s*(?:Saga|Trilogía|Trilogia|Serie|Vol\.?|Volume)\s+.+$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AudibleCodeRegex = new(
        @"\[B[A-Z0-9]+\]",
        RegexOptions.Compiled);

    private static readonly Regex MultiSpaceRegex = new(
        @"\s{2,}",
        RegexOptions.Compiled);

    private readonly ILogger<OnlineCoverFetcher> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="OnlineCoverFetcher"/> class.
    /// </summary>
    public OnlineCoverFetcher(ILogger<OnlineCoverFetcher> logger)
    {
        _logger = logger;
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        };
        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "SmartCovers/5.0 (https://github.com/GeiserX/smart-covers)");
        return client;
    }

    /// <summary>
    /// Extracts a clean title and optional author from the item's metadata.
    /// Handles common audiobook filename patterns like format tags, year suffixes,
    /// series indicators, and "Title - Author" splitting.
    /// </summary>
    internal static (string Title, string? Author) ParseBookInfo(BaseItem item)
    {
        var raw = item.Name;
        if (string.IsNullOrWhiteSpace(raw))
        {
            raw = Path.GetFileNameWithoutExtension(item.Path);
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            return (string.Empty, null);
        }

        // Extract author from "(year, author)" pattern before cleaning removes it.
        // e.g. "A solas (2019, Silvia Congost)" → author = "Silvia Congost"
        string? parenAuthor = null;
        var yearAuthorMatch = ParenYearAuthorRegex.Match(raw);
        if (yearAuthorMatch.Success)
        {
            parenAuthor = yearAuthorMatch.Groups[2].Value.Trim();
            // Remove the full match so CleanText doesn't choke on the remnant
            raw = raw.Remove(yearAuthorMatch.Index, yearAuthorMatch.Length);
        }

        var cleaned = CleanText(raw);

        // Split on " - " to separate title from author.
        // Use the LAST occurrence since titles may contain dashes.
        var dashIdx = cleaned.LastIndexOf(" - ", StringComparison.Ordinal);
        if (dashIdx > 2 && dashIdx < cleaned.Length - 3)
        {
            var title = cleaned[..dashIdx].Trim();
            var author = cleaned[(dashIdx + 3)..].Trim();

            // Also clean the author part (might still have tags)
            author = CleanText(author);

            if (!string.IsNullOrEmpty(title) && !string.IsNullOrEmpty(author))
            {
                return (title, author);
            }
        }

        // Try Jellyfin's own metadata for author (AudioBook/Audio items may have it)
        var metadataAuthor = GetItemAuthor(item);

        // If we found an author from metadata, remove it from the cleaned title
        if (!string.IsNullOrEmpty(metadataAuthor))
        {
            var authorIdx = cleaned.IndexOf(metadataAuthor, StringComparison.OrdinalIgnoreCase);
            if (authorIdx >= 0)
            {
                cleaned = cleaned.Remove(authorIdx, metadataAuthor.Length).Trim();
            }

            return (cleaned, metadataAuthor);
        }

        // Fallback: use author extracted from (year, author) pattern
        return (cleaned.Trim(), parenAuthor);
    }

    /// <summary>
    /// Tries to extract an author name from Jellyfin's own item metadata
    /// (AlbumArtists, Artists, or People with PersonType "Author").
    /// </summary>
    private static string? GetItemAuthor(BaseItem item)
    {
        // AudioBook items may store the narrator/author in AlbumArtists
        if (item is MediaBrowser.Controller.Entities.Audio.IHasAlbumArtist albumItem)
        {
            var artist = albumItem.AlbumArtists?.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(artist))
            {
                return artist;
            }
        }

        if (item is MediaBrowser.Controller.Entities.Audio.IHasArtist artistItem)
        {
            var artist = artistItem.Artists?.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(artist))
            {
                return artist;
            }
        }

        return null;
    }

    /// <summary>
    /// Attempts to fetch a cover image from online sources.
    /// Tries Open Library first, then Google Books as a fallback.
    /// </summary>
    public async Task<(MemoryStream Stream, ImageFormat Format)?> FetchCoverAsync(
        string title, string? author, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var result = await TryOpenLibraryAsync(title, author, cancellationToken).ConfigureAwait(false);
        if (result != null)
        {
            return result;
        }

        result = await TryGoogleBooksAsync(title, author, cancellationToken).ConfigureAwait(false);
        if (result != null)
        {
            return result;
        }

        // Retry with title only (no author) for broader matching
        if (!string.IsNullOrEmpty(author))
        {
            result = await TryOpenLibraryAsync(title, null, cancellationToken).ConfigureAwait(false);
            if (result != null)
            {
                return result;
            }

            result = await TryGoogleBooksAsync(title, null, cancellationToken).ConfigureAwait(false);
            if (result != null)
            {
                return result;
            }
        }

        return null;
    }

    /// <summary>
    /// Attempts to fetch a cover using the item's OriginalTitle (often the English
    /// title for non-English libraries). Called as a final fallback when searches
    /// with the localized title fail.
    /// </summary>
    public async Task<(MemoryStream Stream, ImageFormat Format)?> FetchCoverByOriginalTitleAsync(
        BaseItem item, CancellationToken cancellationToken)
    {
        var originalTitle = item.OriginalTitle;
        if (string.IsNullOrWhiteSpace(originalTitle)
            || string.Equals(originalTitle, item.Name, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        _logger.LogDebug("Retrying online cover fetch with OriginalTitle: '{Title}'", originalTitle);
        var (cleanTitle, author) = ParseBookInfo(item);

        // Use the original title but keep the parsed author
        return await FetchCoverAsync(originalTitle, author, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(MemoryStream Stream, ImageFormat Format)?> TryOpenLibraryAsync(
        string title, string? author, CancellationToken ct)
    {
        try
        {
            var query = $"title={Uri.EscapeDataString(title)}";
            if (!string.IsNullOrEmpty(author))
            {
                query += $"&author={Uri.EscapeDataString(author)}";
            }

            var searchUrl = $"https://openlibrary.org/search.json?{query}&limit=5&fields=title,author_name,cover_i";
            _logger.LogDebug("Open Library search: {Url}", searchUrl);

            var json = await Http.GetStringAsync(searchUrl, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("docs", out var docs))
            {
                return null;
            }

            int? coverId = null;
            foreach (var book in docs.EnumerateArray())
            {
                if (book.TryGetProperty("cover_i", out var coverProp)
                    && coverProp.ValueKind == JsonValueKind.Number)
                {
                    coverId = coverProp.GetInt32();
                    break;
                }
            }

            if (coverId == null)
            {
                _logger.LogDebug("Open Library: no cover found for '{Title}'", title);
                return null;
            }

            // default=false returns 404 instead of a 1x1 blank placeholder when no cover exists
            var coverUrl = $"https://covers.openlibrary.org/b/id/{coverId}-L.jpg?default=false";
            _logger.LogDebug("Open Library cover URL: {Url}", coverUrl);

            var imageBytes = await Http.GetByteArrayAsync(coverUrl, ct).ConfigureAwait(false);
            return ValidateImage(imageBytes, $"Open Library (cover {coverId})");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Open Library fetch failed for '{Title}'", title);
            return null;
        }
    }

    private async Task<(MemoryStream Stream, ImageFormat Format)?> TryGoogleBooksAsync(
        string title, string? author, CancellationToken ct)
    {
        try
        {
            // Escape title and author individually; keep intitle:/inauthor: operators
            // and + separator as literal characters in the URL.
            var q = !string.IsNullOrEmpty(author)
                ? $"intitle:{Uri.EscapeDataString(title)}+inauthor:{Uri.EscapeDataString(author)}"
                : Uri.EscapeDataString(title);

            var searchUrl = $"https://www.googleapis.com/books/v1/volumes?q={q}&maxResults=3";
            _logger.LogDebug("Google Books search: {Url}", searchUrl);

            var json = await Http.GetStringAsync(searchUrl, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("items", out var items))
            {
                _logger.LogDebug("Google Books: no results for '{Title}'", title);
                return null;
            }

            string? thumbnailUrl = null;
            foreach (var item in items.EnumerateArray())
            {
                if (item.TryGetProperty("volumeInfo", out var vi)
                    && vi.TryGetProperty("imageLinks", out var links))
                {
                    // Prefer larger images first
                    if (links.TryGetProperty("large", out var large))
                    {
                        thumbnailUrl = large.GetString();
                    }
                    else if (links.TryGetProperty("medium", out var medium))
                    {
                        thumbnailUrl = medium.GetString();
                    }
                    else if (links.TryGetProperty("thumbnail", out var thumb))
                    {
                        thumbnailUrl = thumb.GetString();
                    }
                    else if (links.TryGetProperty("smallThumbnail", out var small))
                    {
                        thumbnailUrl = small.GetString();
                    }

                    if (thumbnailUrl != null)
                    {
                        break;
                    }
                }
            }

            if (string.IsNullOrEmpty(thumbnailUrl))
            {
                _logger.LogDebug("Google Books: no thumbnail for '{Title}'", title);
                return null;
            }

            // Upgrade to highest resolution: zoom=0, remove page curl effect
            var highResUrl = thumbnailUrl
                .Replace("zoom=1", "zoom=0")
                .Replace("zoom=5", "zoom=0")
                .Replace("&edge=curl", "");

            // Upgrade HTTP to HTTPS
            if (highResUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                highResUrl = string.Concat("https://", highResUrl.AsSpan(7));
            }

            _logger.LogDebug("Google Books cover URL: {Url}", highResUrl);

            var imageBytes = await Http.GetByteArrayAsync(highResUrl, ct).ConfigureAwait(false);
            return ValidateImage(imageBytes, "Google Books");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Google Books fetch failed for '{Title}'", title);
            return null;
        }
    }

    private (MemoryStream Stream, ImageFormat Format)? ValidateImage(byte[] data, string source)
    {
        // Skip tiny images — placeholders, 1x1 transparent PNGs, etc.
        if (data.Length < 1000)
        {
            _logger.LogDebug("{Source}: image too small ({Size} bytes), likely a placeholder", source, data.Length);
            return null;
        }

        var (format, offset) = CoverImageProvider.DetectImageFormat(data);
        if (format == null)
        {
            _logger.LogDebug("{Source}: unrecognized image format, skipping", source);
            return null;
        }

        _logger.LogInformation("Online cover found via {Source}: {Format}, {Size} bytes", source, format, data.Length);

        var stream = offset > 0
            ? new MemoryStream(data, offset, data.Length - offset)
            : new MemoryStream(data);

        return (stream, format.Value);
    }

    private static string CleanText(string text)
    {
        text = FormatTagRegex.Replace(text, "");
        text = LocaleTagRegex.Replace(text, "");
        text = AudibleCodeRegex.Replace(text, "");
        text = ParenYearRegex.Replace(text, "");
        text = TrailingYearRegex.Replace(text, "");
        text = SagaRegex.Replace(text, "");
        text = MultiSpaceRegex.Replace(text, " ");
        return text.Trim();
    }
}
