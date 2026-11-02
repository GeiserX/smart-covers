using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Model.Drawing;
using Microsoft.Extensions.Logging;

namespace SmartCovers;

/// <summary>
/// Fetches book/audiobook covers from online sources (Open Library, Google Books)
/// as a last-resort fallback when local extraction methods fail.
/// </summary>
public class OnlineCoverFetcher
{
    private static readonly HttpClient DefaultHttp = CreateHttpClient();

    private static readonly Regex FormatTagRegex = new(
        @"\((?:Mp3|M4[ab]|FLAC|OGG|Opus|WAV|WMA|AAC|mp3)[\s\-]*[^)]*\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex LocaleTagRegex = new(
        @"\[(?:Castellano|Español|Latino|English|Narración)[^\]]*\]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // " - 1999" at the end. Not the second year of a range: "almeriense, 1752-1910".
    private static readonly Regex TrailingYearRegex = new(
        @"(?<!\d)\s*[-–—]\s*\d{4}\s*$",
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

    // A leading bracket tag that only says what the file is: "[Audiolibro mp3] Title".
    private static readonly Regex BracketFormatPrefixRegex = new(
        @"^\s*\[[^\]]*(?:mp3|m4a|m4b|flac|audiolibro|audiobook)[^\]]*\]\s*[-–—]?\s*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // A publication year hanging off the end of an author: "Ken Follett 1989".
    // Only ever applied to the author — a title may legitimately end in a year.
    private static readonly Regex TrailingBareYearRegex = new(
        @"\s+(?:19|20)\d{2}\s*$",
        RegexOptions.Compiled);

    // An edition clause: ", Second Edition", "(2nd Edition)", "(The Expanded Edition)".
    // The qualifier before "Edition" is required, and so is the comma or bracket that
    // introduces it, so a title that simply contains the word ("The Paris Edition")
    // keeps it.
    private static readonly Regex EditionClauseRegex = new(
        @"\s*[,(\[]\s*(?:the\s+)?"
        + @"(?:\d{1,2}(?:st|nd|rd|th)|first|second|third|fourth|fifth|sixth|seventh|eighth|ninth|tenth"
        + @"|revised|expanded|updated|anniversary|special|deluxe|new)"
        + @"(?:\s+(?:and|&)\s+\w+)?\s+edition\s*[)\]]?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // A narration tag: "(Unabridged)", "[Unabridged]", or a trailing bare "Unabridged".
    private static readonly Regex NarrationTagRegex = new(
        @"\s*(?:[(\[]\s*(?:un)?abridged\s*[)\]]|[-–—]?\s*(?:un)?abridged\s*$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // An extension that leaked into the item name: "Crossing the Chasm.m4b".
    private static readonly Regex MediaExtensionSuffixRegex = new(
        @"\.(?:mp3|m4a|m4b|flac|ogg|opus|wma|aac|wav|epub|pdf|mobi|azw3?|prc|cbz|cbr)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // A dash with a space on at least one side: " - ", "- ", " -".
    private static readonly Regex SpacedDashRegex = new(
        @"\s+[-–—]\s*|\s*[-–—]\s+",
        RegexOptions.Compiled);

    // A free-text search with no author to check is only trusted for a title this
    // long; "Hacia la Fundación" alone also matches unrelated books.
    private const int MinFreeTextTitleWords = 4;

    private static readonly TimeSpan GoogleBooksPause = TimeSpan.FromHours(6);

    private readonly ILogger<OnlineCoverFetcher> _logger;
    private readonly HttpClient _http;
    private readonly object _googlePauseLock = new();
    private DateTime _googlePausedUntil = DateTime.MinValue;
    private string _googlePausedKey = string.Empty;

    /// <summary>
    /// Where the Google Books API key comes from. Tests replace it.
    /// </summary>
    internal Func<string?> GoogleBooksApiKeySource { get; set; } = static () => Plugin.Instance?.Configuration?.GoogleBooksApiKey;

    /// <summary>
    /// Initializes a new instance of the <see cref="OnlineCoverFetcher"/> class.
    /// </summary>
    public OnlineCoverFetcher(ILogger<OnlineCoverFetcher> logger)
        : this(logger, DefaultHttp)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="OnlineCoverFetcher"/> class
    /// with an explicit <see cref="HttpClient"/> (used for testing).
    /// </summary>
    internal OnlineCoverFetcher(ILogger<OnlineCoverFetcher> logger, HttpClient httpClient)
    {
        _logger = logger;
        _http = httpClient;
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
    /// <param name="item">The item being given a cover.</param>
    /// <param name="identityName">
    /// The book folder's name, when the item is one track of a folder audiobook.
    /// A track is named "01" or "Pista 1", which identifies nothing — the book's
    /// name is on the folder, so it wins over the item's own name when supplied.
    /// </param>
    internal static (string Title, string? Author) ParseBookInfo(BaseItem item, string? identityName = null)
    {
        var query = ParseBookQuery(item, identityName);
        return (query.Title, query.Author);
    }

    /// <summary>
    /// Everything the name and tags say about the book: title, author, ISBN, OCLC.
    /// </summary>
    internal static BookQuery ParseBookQuery(BaseItem item, string? identityName = null)
    {
        var raw = !string.IsNullOrWhiteSpace(identityName) ? identityName : item.Name;
        var fileName = string.IsNullOrEmpty(item.Path) ? null : Path.GetFileNameWithoutExtension(item.Path);
        if (string.IsNullOrWhiteSpace(raw))
        {
            raw = fileName;
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            return new BookQuery(string.Empty, null, []);
        }

        // An Anna's Archive file name keeps its ISBN even after Jellyfin replaces the
        // item's Name with the clean title from the book's own metadata.
        AnnasArchiveName? fromFile = null;
        if (fileName != null && !string.Equals(fileName, raw, StringComparison.Ordinal))
        {
            BookNameParser.TryParseAnnasArchive(fileName, out fromFile);
        }

        if (BookNameParser.TryParseAnnasArchive(raw, out var annas) && annas != null)
        {
            return new BookQuery(
                CleanText(annas.Title),
                annas.Author,
                annas.Isbn != null ? [annas.Isbn] : [],
                annas.Oclc);
        }

        var isbns = fromFile?.Isbn != null ? new[] { fromFile.Isbn } : [];

        // A track's own tags name the book better than a folder name full of
        // "(mp3)" and years: Album is the book, the artist its author.
        if (item is AudioBook track && TagTitle(track) is { } albumTitle)
        {
            var tagAuthor = TagAuthor(track) ?? ParseName(raw, item).Author;
            return new BookQuery(albumTitle, tagAuthor, isbns, fromFile?.Oclc);
        }

        var (title, author) = ParseName(raw, item);
        return new BookQuery(title, author, isbns, fromFile?.Oclc);
    }

    /// <summary>
    /// The Album tag of a track, when it reads as a book title.
    /// </summary>
    private static string? TagTitle(Audio track)
    {
        var album = track.Album;
        if (string.IsNullOrWhiteSpace(album) || BookIdentity.IsDiscFolderName(album))
        {
            return null;
        }

        var cleaned = CleanText(MediaExtensionSuffixRegex.Replace(album, string.Empty));
        return IsSearchableTitle(cleaned) ? cleaned : null;
    }

    private static string? TagAuthor(Audio track)
    {
        var tag = track.AlbumArtists?.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a))
            ?? track.Artists?.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a));
        return BookNameParser.AuthorFromTag(tag);
    }

    /// <summary>
    /// Title and author from a free-form name: "Title (mp3) Author Year",
    /// "Title - Author", "Title- Author 2004", "Authors - Title".
    /// </summary>
    internal static (string Title, string? Author) ParseName(string raw, BaseItem? item = null)
    {
        // Extract author from "(year, author)" pattern before cleaning removes it.
        // e.g. "A solas (2019, Silvia Congost)" → author = "Silvia Congost"
        raw = MediaExtensionSuffixRegex.Replace(raw, string.Empty);
        raw = BracketFormatPrefixRegex.Replace(raw, string.Empty);

        string? parenAuthor = null;
        var yearAuthorMatch = ParenYearAuthorRegex.Match(raw);
        if (yearAuthorMatch.Success)
        {
            parenAuthor = yearAuthorMatch.Groups[2].Value.Trim();
            // Remove the full match so CleanText doesn't choke on the remnant
            raw = raw.Remove(yearAuthorMatch.Index, yearAuthorMatch.Length);
        }

        // "<Title> (mp3) <Author> <Year>" is how most of these folders are named, and
        // the format tag marks the boundary: title before it, author after it. Doing
        // this first stops the whole name collapsing into one unsearchable string.
        var formatTag = FormatTagRegex.Match(raw);
        if (formatTag.Success && formatTag.Index > 2)
        {
            var beforeTag = CleanText(raw[..formatTag.Index]);
            var afterTag = CleanText(raw[(formatTag.Index + formatTag.Length)..])
                .TrimStart(' ', '-', '–', '—')
                .Trim();
            afterTag = TrailingBareYearRegex.Replace(afterTag, string.Empty).Trim();

            if (IsSearchableTitle(beforeTag))
            {
                return (beforeTag, string.IsNullOrWhiteSpace(afterTag) ? null : BookNameParser.FirstAuthor(afterTag));
            }
        }

        var cleaned = CleanText(raw);

        // Split title from author at the LAST dash with a space on at least one side
        // ("Title - Author", "Title- Author"), since titles may contain dashes. A dash
        // inside a word ("Pérez-Reverte") is never a split.
        var split = SpacedDashRegex.Matches(cleaned).LastOrDefault();
        if (split != null && split.Index > 2 && split.Index + split.Length < cleaned.Length - 2)
        {
            var title = cleaned[..split.Index].Trim();
            var author = cleaned[(split.Index + split.Length)..].Trim();

            // Also clean the author part (might still have tags, or a year)
            author = TrailingBareYearRegex.Replace(CleanText(author), string.Empty).Trim();

            // "Douglas Stone, Bruce Patton, and Sheila Heen - Difficult Conversations":
            // a list of people on the left is the author, whatever the order says.
            if (BookNameParser.LooksLikeAuthorList(title) && !BookNameParser.LooksLikeAuthorList(author))
            {
                (title, author) = (author, title);
            }

            if (!string.IsNullOrEmpty(title) && !string.IsNullOrEmpty(author))
            {
                return (title, BookNameParser.FirstAuthor(author));
            }
        }

        // Try Jellyfin's own metadata for author (AudioBook/Audio items may have it)
        var metadataAuthor = item == null ? null : GetItemAuthor(item);

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
    /// (AlbumArtists or Artists on audio items).
    /// </summary>
    private static string? GetItemAuthor(BaseItem item)
    {
        // AudioBook items may store the narrator/author in AlbumArtists
        if (item is MediaBrowser.Controller.Entities.Audio.IHasAlbumArtist albumItem)
        {
            var artist = albumItem.AlbumArtists?.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(artist))
            {
                return BookNameParser.AuthorFromTag(artist);
            }
        }

        if (item is MediaBrowser.Controller.Entities.Audio.IHasArtist artistItem)
        {
            var artist = artistItem.Artists?.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(artist))
            {
                return BookNameParser.AuthorFromTag(artist);
            }
        }

        return null;
    }

    /// <summary>
    /// Title and artist of a music track, for a drawn cover only: tags when the
    /// track has them, else "Artist - Title" from the name.
    /// </summary>
    internal static (string Title, string? Artist) ParseMusicTrack(BaseItem item)
    {
        var name = !string.IsNullOrWhiteSpace(item.Name)
            ? item.Name
            : Path.GetFileNameWithoutExtension(item.Path ?? string.Empty);
        name = MediaExtensionSuffixRegex.Replace(name ?? string.Empty, string.Empty).Trim();

        var dash = name.LastIndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0 && dash < name.Length - 3)
        {
            return (name[(dash + 3)..].Trim(), name[..dash].Trim());
        }

        var artist = item is IHasArtist hasArtist ? hasArtist.Artists?.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a)) : null;
        return (name, artist);
    }

    /// <summary>
    /// Whether a title is worth searching a book catalogue for. A bare track number
    /// is not: searching Open Library for "2" returns half a million books and the
    /// plugin would ship the cover of whichever one came back first.
    /// </summary>
    internal static bool IsSearchableTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        title = title.Trim();
        return title.Length >= 3 && title.Any(char.IsLetter);
    }

    /// <summary>
    /// Attempts to fetch a cover image from online sources.
    /// Tries Open Library first, then Google Books as a fallback.
    /// </summary>
    public async Task<(MemoryStream Stream, ImageFormat Format)?> FetchCoverAsync(
        string title, string? author, CancellationToken cancellationToken)
        => (await LookupAsync(new BookQuery(title, author, []), cancellationToken).ConfigureAwait(false)).Cover;

    /// <summary>
    /// Looks the book up and says how it went: the cover, or why there is none.
    /// ISBNs first (an exact edition), then title and author searches.
    /// </summary>
    internal async Task<OnlineLookup> LookupAsync(BookQuery query, CancellationToken cancellationToken)
    {
        var ctx = new LookupContext();

        foreach (var isbn in query.Isbns.Distinct(StringComparer.Ordinal))
        {
            var byIsbn = await TryCoverUrlAsync(
                $"https://covers.openlibrary.org/b/isbn/{isbn}-L.jpg?default=false",
                $"Open Library (ISBN {isbn})",
                CatalogueSource.OpenLibrary,
                ctx,
                cancellationToken).ConfigureAwait(false);
            byIsbn ??= await TryGoogleBooksAsync($"isbn:{isbn}", $"isbn {isbn}", ctx, cancellationToken).ConfigureAwait(false);
            if (byIsbn != null)
            {
                return ctx.Found(byIsbn);
            }
        }

        if (!string.IsNullOrEmpty(query.Oclc))
        {
            var byOclc = await TryCoverUrlAsync(
                $"https://covers.openlibrary.org/b/oclc/{query.Oclc}-L.jpg?default=false",
                $"Open Library (OCLC {query.Oclc})",
                CatalogueSource.OpenLibrary,
                ctx,
                cancellationToken).ConfigureAwait(false);
            if (byOclc != null)
            {
                return ctx.Found(byOclc);
            }
        }

        var title = query.Title;
        if (!IsSearchableTitle(title))
        {
            return ctx.Missed();
        }

        var author = BookNameParser.FirstAuthor(query.Author);

        var attempts = new List<(string Title, string? Author)> { (title, author) };
        if (!string.IsNullOrEmpty(author))
        {
            // Retry with title only for broader matching.
            attempts.Add((title, null));

            // Plenty of folders are named "<Author> - <Title>", and nothing in the
            // name says which half is which. Rather than guess, try it the other
            // way round once the first reading has come up empty.
            if (IsSearchableTitle(author))
            {
                attempts.Add((author, BookNameParser.FirstAuthor(title)));
            }
        }

        // Last try: the main title without its subtitle. Catalogues index
        // "Four Thousand Weeks", not "Four Thousand Weeks: Time Management for
        // Mortals". Guarded to multi-word main titles — a one-word one like
        // "Build" matches tens of thousands of books and would fetch a stranger.
        var mainTitle = MainTitleOf(title);
        if (mainTitle != null)
        {
            attempts.Add((mainTitle, author));
        }

        foreach (var (attemptTitle, attemptAuthor) in attempts)
        {
            var result = await TryAttemptAsync(attemptTitle, attemptAuthor, ctx, cancellationToken).ConfigureAwait(false);
            if (result != null)
            {
                return ctx.Found(result);
            }
        }

        return ctx.Missed();
    }

    /// <summary>
    /// One reading of the book, asked three ways: Open Library's title and author
    /// fields, Open Library's free-text search, then Google Books.
    /// </summary>
    private async Task<(MemoryStream Stream, ImageFormat Format)?> TryAttemptAsync(
        string title, string? author, LookupContext ctx, CancellationToken ct)
    {
        var fields = $"title={Uri.EscapeDataString(title)}";
        if (!string.IsNullOrEmpty(author))
        {
            fields += $"&author={Uri.EscapeDataString(author)}";
        }

        var result = await TryOpenLibrarySearchAsync(fields, null, title, ctx, ct).ConfigureAwait(false);
        if (result != null)
        {
            return result;
        }

        // The title= field matches work titles, usually the original English ones,
        // so "Hacia la Fundación" by Isaac Asimov finds nothing there while a
        // free-text search finds "Forward the Foundation". A free-text hit must name
        // our author, so a stranger's book with a similar title is never taken.
        // Without an author, only a long title is specific enough to trust.
        if (!string.IsNullOrEmpty(author))
        {
            result = await TryOpenLibrarySearchAsync(
                $"q={Uri.EscapeDataString($"{title} {author}")}", author, title, ctx, ct).ConfigureAwait(false);
        }
        else if (BookNameParser.WordCount(title) >= MinFreeTextTitleWords)
        {
            result = await TryOpenLibrarySearchAsync(
                $"q={Uri.EscapeDataString(title)}", null, title, ctx, ct).ConfigureAwait(false);
        }

        if (result != null)
        {
            return result;
        }

        // Escape title and author individually; keep intitle:/inauthor: operators
        // and + separator as literal characters in the URL.
        var q = !string.IsNullOrEmpty(author)
            ? $"intitle:{Uri.EscapeDataString(title)}+inauthor:{Uri.EscapeDataString(author)}"
            : Uri.EscapeDataString(title);

        return await TryGoogleBooksAsync(q, title, ctx, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The part of a title before its subtitle, when that is a title in its own
    /// right: at least two words, and different from what was already searched.
    /// A subtitle starts at a colon, at ". " ("La Vasconie. Étude historique..."),
    /// or at ", " when three or more words follow. Returns null when there is no
    /// useful subtitle to drop.
    /// </summary>
    internal static string? MainTitleOf(string title)
    {
        var whole = title.Trim();
        var cuts = new List<int>();

        var colon = whole.IndexOf(':', StringComparison.Ordinal);
        if (colon > 0)
        {
            cuts.Add(colon);
        }

        var period = whole.IndexOf(". ", StringComparison.Ordinal);
        if (period > 0)
        {
            cuts.Add(period);
        }

        var comma = whole.IndexOf(", ", StringComparison.Ordinal);
        if (comma > 0 && BookNameParser.WordCount(whole[(comma + 2)..]) >= 3)
        {
            cuts.Add(comma);
        }

        foreach (var cut in cuts.Order())
        {
            var main = whole[..cut].Trim();
            if (main.Length < whole.Length
                && IsSearchableTitle(main)
                && BookNameParser.WordCount(main) >= 2)
            {
                return main;
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
        => (await LookupByOriginalTitleAsync(item, null, cancellationToken).ConfigureAwait(false))?.Cover;

    /// <summary>
    /// The lookup by OriginalTitle, or null when the item has no distinct one.
    /// </summary>
    internal async Task<OnlineLookup?> LookupByOriginalTitleAsync(
        BaseItem item, string? author, CancellationToken cancellationToken)
    {
        var originalTitle = item.OriginalTitle;
        if (string.IsNullOrWhiteSpace(originalTitle)
            || string.Equals(originalTitle, item.Name, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        _logger.LogDebug("Retrying online cover fetch with OriginalTitle: '{Title}'", originalTitle);
        author ??= ParseBookInfo(item).Author;

        // Use the original title but keep the parsed author
        return await LookupAsync(new BookQuery(originalTitle, author, []), cancellationToken).ConfigureAwait(false);
    }

    private async Task<(MemoryStream Stream, ImageFormat Format)?> TryOpenLibrarySearchAsync(
        string query, string? requiredAuthor, string title, LookupContext ctx, CancellationToken ct)
    {
        var searchUrl = $"https://openlibrary.org/search.json?{query}&limit=5&fields=title,author_name,cover_i";
        if (!ctx.Asked.Add(searchUrl))
        {
            return null;
        }

        _logger.LogDebug("Open Library search: {Url}", searchUrl);

        var body = await GetAsync(searchUrl, CatalogueSource.OpenLibrary, ctx, ct).ConfigureAwait(false);
        if (body == null)
        {
            return null;
        }

        int? coverId = null;
        try
        {
            using var doc = JsonDocument.Parse(body.Value.Bytes);
            if (doc.RootElement.TryGetProperty("docs", out var docs) && docs.ValueKind == JsonValueKind.Array)
            {
                foreach (var book in docs.EnumerateArray())
                {
                    if (!book.TryGetProperty("cover_i", out var coverProp) || coverProp.ValueKind != JsonValueKind.Number)
                    {
                        continue;
                    }

                    if (requiredAuthor != null && !BookNameParser.SharesAuthorName(AuthorsOf(book), requiredAuthor))
                    {
                        continue;
                    }

                    coverId = coverProp.GetInt32();
                    break;
                }
            }
        }
        catch (JsonException ex)
        {
            ctx.Fail(CatalogueSource.OpenLibrary, "Open Library: unreadable reply");
            _logger.LogDebug(ex, "Open Library returned an unreadable reply for '{Title}'", title);
            return null;
        }

        if (coverId == null)
        {
            _logger.LogDebug("Open Library: no cover found for '{Title}'", title);
            return null;
        }

        // default=false returns 404 instead of a 1x1 blank placeholder when no cover exists
        return await TryCoverUrlAsync(
            $"https://covers.openlibrary.org/b/id/{coverId}-L.jpg?default=false",
            $"Open Library (cover {coverId})",
            CatalogueSource.OpenLibrary,
            ctx,
            ct).ConfigureAwait(false);
    }

    private static IEnumerable<string> AuthorsOf(JsonElement book)
    {
        if (book.TryGetProperty("author_name", out var names) && names.ValueKind == JsonValueKind.Array)
        {
            foreach (var name in names.EnumerateArray())
            {
                if (name.ValueKind == JsonValueKind.String && name.GetString() is { } value)
                {
                    yield return value;
                }
            }
        }
    }

    private async Task<(MemoryStream Stream, ImageFormat Format)?> TryGoogleBooksAsync(
        string q, string title, LookupContext ctx, CancellationToken ct)
    {
        var key = GoogleBooksApiKeySource();
        if (IsGoogleBooksPaused(key))
        {
            ctx.Note("Google Books skipped: quota exhausted");
            return null;
        }

        var searchUrl = $"https://www.googleapis.com/books/v1/volumes?q={q}&maxResults=3";
        if (!ctx.Asked.Add(searchUrl))
        {
            return null;
        }

        _logger.LogDebug("Google Books search: {Url}", searchUrl);

        var requestUrl = string.IsNullOrWhiteSpace(key) ? searchUrl : $"{searchUrl}&key={Uri.EscapeDataString(key.Trim())}";
        var body = await GetAsync(requestUrl, CatalogueSource.GoogleBooks, ctx, ct).ConfigureAwait(false);
        if (body == null)
        {
            return null;
        }

        string? thumbnailUrl = null;
        try
        {
            using var doc = JsonDocument.Parse(body.Value.Bytes);

            if (!doc.RootElement.TryGetProperty("items", out var items))
            {
                _logger.LogDebug("Google Books: no results for '{Title}'", title);
                return null;
            }

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
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "Google Books returned an unreadable reply for '{Title}'", title);
            return null;
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
        return await TryCoverUrlAsync(highResUrl, "Google Books", CatalogueSource.GoogleBooks, ctx, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Downloads a cover image. A 404 is a plain miss; Open Library's blank
    /// placeholder (a tiny GIF) is rejected.
    /// </summary>
    private async Task<(MemoryStream Stream, ImageFormat Format)?> TryCoverUrlAsync(
        string url, string label, CatalogueSource source, LookupContext ctx, CancellationToken ct)
    {
        _logger.LogDebug("{Source} cover URL: {Url}", label, url);

        var body = await GetAsync(url, source, ctx, ct).ConfigureAwait(false);
        if (body == null)
        {
            return null;
        }

        if (source == CatalogueSource.OpenLibrary
            && string.Equals(body.Value.ContentType, "image/gif", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("{Source}: placeholder image, no cover", label);
            return null;
        }

        var image = ValidateImage(body.Value.Bytes, label);
        if (image != null)
        {
            ctx.Source = source == CatalogueSource.OpenLibrary ? "Open Library" : "Google Books";
        }

        return image;
    }

    /// <summary>
    /// GETs a URL. Returns null on any failure, after recording on the context
    /// whether that was a miss (404) or the catalogue being unreachable.
    /// </summary>
    private async Task<(byte[] Bytes, string? ContentType)?> GetAsync(
        string url, CatalogueSource source, LookupContext ctx, CancellationToken ct)
    {
        var name = source == CatalogueSource.OpenLibrary ? "Open Library" : "Google Books";
        try
        {
            using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                return (bytes, response.Content.Headers.ContentType?.MediaType);
            }

            var status = response.StatusCode;
            if (status == HttpStatusCode.NotFound)
            {
                return null;
            }

            if (source == CatalogueSource.GoogleBooks
                && (status == HttpStatusCode.TooManyRequests || status == HttpStatusCode.Forbidden))
            {
                PauseGoogleBooks(status);
                ctx.Note($"Google Books: {(int)status} {status}, quota exhausted");
                return null;
            }

            ctx.Fail(source, $"{name}: {(int)status} {status}");
            return null;
        }
        catch (HttpRequestException ex)
        {
            ctx.Fail(source, $"{name}: {ex.Message}");
            _logger.LogDebug(ex, "{Source} request failed: {Url}", name, StripKey(url));
            return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            ctx.Fail(source, $"{name}: timed out");
            _logger.LogDebug("{Source} request timed out: {Url}", name, StripKey(url));
            return null;
        }
    }

    private static string StripKey(string url)
    {
        var idx = url.IndexOf("&key=", StringComparison.Ordinal);
        return idx < 0 ? url : url[..idx];
    }

    private bool IsGoogleBooksPaused(string? key)
    {
        lock (_googlePauseLock)
        {
            return DateTime.UtcNow < _googlePausedUntil
                && string.Equals(_googlePausedKey, key ?? string.Empty, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Stops asking Google Books for a while after it refuses for quota. Without a
    /// key its shared daily quota is often zero, and asking again for each of
    /// hundreds of items only slows the run. A new key lifts the pause at once.
    /// </summary>
    private void PauseGoogleBooks(HttpStatusCode status)
    {
        var key = GoogleBooksApiKeySource() ?? string.Empty;
        lock (_googlePauseLock)
        {
            if (DateTime.UtcNow < _googlePausedUntil && string.Equals(_googlePausedKey, key, StringComparison.Ordinal))
            {
                return;
            }

            _googlePausedUntil = DateTime.UtcNow + GoogleBooksPause;
            _googlePausedKey = key;
        }

        _logger.LogWarning(
            "Google Books refused a lookup ({Status}): its quota is exhausted{Hint}. Google Books is skipped for the next {Hours} hours",
            (int)status,
            key.Length == 0 ? "; set a Google Books API key in the SmartCovers settings" : " for this API key",
            GoogleBooksPause.TotalHours);
    }

    internal (MemoryStream Stream, ImageFormat Format)? ValidateImage(byte[] data, string source)
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

    internal static string CleanText(string text)
    {
        text = FormatTagRegex.Replace(text, "");
        text = LocaleTagRegex.Replace(text, "");
        text = BookNameParser.OcrTag.Replace(text, "");
        text = EditionClauseRegex.Replace(text, "");
        text = NarrationTagRegex.Replace(text, "");
        text = AudibleCodeRegex.Replace(text, "");
        text = ParenYearRegex.Replace(text, "");
        text = TrailingYearRegex.Replace(text, "");
        text = SagaRegex.Replace(text, "");
        text = MultiSpaceRegex.Replace(text, " ");
        return text.Trim();
    }
}

/// <summary>
/// What to look a book up by.
/// </summary>
/// <param name="Title">The title.</param>
/// <param name="Author">The author, or null.</param>
/// <param name="Isbns">ISBNs to try first, each already checksum-valid.</param>
/// <param name="Oclc">An OCLC number, or null.</param>
internal sealed record BookQuery(string Title, string? Author, IReadOnlyList<string> Isbns, string? Oclc = null);

/// <summary>
/// The result of an online lookup: the cover, or why there is none.
/// </summary>
/// <param name="Cover">The cover, or null.</param>
/// <param name="NetworkError">True when Open Library could not be asked (timeout, 429, 5xx), so a miss proves nothing.</param>
/// <param name="Detail">What went wrong, for the log ("Open Library: 429 TooManyRequests").</param>
/// <param name="Source">"Open Library" or "Google Books" when a cover was found.</param>
internal sealed record OnlineLookup(
    (MemoryStream Stream, ImageFormat Format)? Cover, bool NetworkError, string? Detail, string? Source);

/// <summary>
/// Which catalogue a request went to.
/// </summary>
internal enum CatalogueSource
{
    /// <summary>openlibrary.org and covers.openlibrary.org.</summary>
    OpenLibrary,

    /// <summary>googleapis.com and the books.google.com image host.</summary>
    GoogleBooks
}

/// <summary>
/// The state of one lookup across all its requests.
/// </summary>
internal sealed class LookupContext
{
    /// <summary>Gets the URLs already asked, so a repeated reading is not asked twice.</summary>
    public HashSet<string> Asked { get; } = new(StringComparer.Ordinal);

    /// <summary>Gets a value indicating whether Open Library failed to answer at least once.</summary>
    public bool NetworkError { get; private set; }

    /// <summary>Gets the first thing worth reporting about this lookup.</summary>
    public string? Detail { get; private set; }

    /// <summary>Gets or sets the catalogue the cover came from.</summary>
    public string? Source { get; set; }

    /// <summary>Records a failed request. Only Open Library failures make the miss inconclusive.</summary>
    public void Fail(CatalogueSource source, string detail)
    {
        if (source == CatalogueSource.OpenLibrary)
        {
            // The first Open Library failure is what makes the miss inconclusive, so
            // it is what the log should say, ahead of any Google Books note.
            if (!NetworkError)
            {
                Detail = detail;
            }

            NetworkError = true;
            return;
        }

        Detail ??= detail;
    }

    /// <summary>Records something worth reporting that is not a failure.</summary>
    public void Note(string detail) => Detail ??= detail;

    /// <summary>The lookup found a cover.</summary>
    public OnlineLookup Found((MemoryStream Stream, ImageFormat Format)? cover) => new(cover, false, null, Source);

    /// <summary>The lookup found nothing.</summary>
    public OnlineLookup Missed() => new(null, NetworkError, Detail, null);
}
