using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace SmartCovers.Tests;

/// <summary>
/// A handler that answers the first rule whose predicate matches, in the order the
/// rules were added. Unlike a dictionary-backed mock this is deterministic, which
/// matters when a test needs one specific query to succeed and every other to fail.
/// </summary>
internal sealed class OrderedMockHandler : HttpMessageHandler
{
    private readonly List<(Func<string, bool> Match, Func<HttpResponseMessage> Respond)> _rules = [];

    public List<string> RequestedUrls { get; } = [];

    /// <summary>The requested URIs, kept escaped so query values decode exactly.</summary>
    public List<Uri> RequestedUris { get; } = [];

    /// <summary>
    /// The catalogue lookups that were made, in order, each rendered as the decoded
    /// query it actually asked — "OL title='X' author='Y'", "GB q='X'". Cover image
    /// downloads are left out; this is the search sequence.
    /// </summary>
    public IReadOnlyList<string> LookupSequence => RequestedUris.Select(Describe).OfType<string>().ToList();

    private static string? Describe(Uri uri)
    {
        var query = ParseQuery(uri);

        if (uri.Host.Contains("openlibrary.org", StringComparison.Ordinal)
            && uri.AbsolutePath.Contains("search", StringComparison.Ordinal))
        {
            var title = query.GetValueOrDefault("title", string.Empty);
            return query.TryGetValue("author", out var author)
                ? $"OL title='{title}' author='{author}'"
                : $"OL title='{title}'";
        }

        if (uri.Host.Contains("googleapis.com", StringComparison.Ordinal))
        {
            return $"GB q='{query.GetValueOrDefault("q", string.Empty)}'";
        }

        // A cover image download, not a lookup.
        return null;
    }

    private static Dictionary<string, string> ParseQuery(Uri uri)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var split = pair.IndexOf('=', StringComparison.Ordinal);
            if (split > 0)
            {
                result[Uri.UnescapeDataString(pair[..split])] = Uri.UnescapeDataString(pair[(split + 1)..]);
            }
        }

        return result;
    }

    public void When(Func<string, bool> match, Func<HttpResponseMessage> respond)
        => _rules.Add((match, respond));

    public void WhenUrlContains(string needle, object json)
        => When(
            url => url.Contains(needle, StringComparison.OrdinalIgnoreCase),
            () => Json(json));

    public static HttpResponseMessage Json(object payload)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };

    public static HttpResponseMessage Jpeg(int size)
    {
        var data = new byte[size];
        data[0] = 0xFF; data[1] = 0xD8; data[2] = 0xFF; data[3] = 0xE0;
        var msg = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };
        msg.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
        return msg;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri?.ToString() ?? string.Empty;
        RequestedUrls.Add(url);

        if (request.RequestUri is not null)
        {
            RequestedUris.Add(request.RequestUri);
        }

        foreach (var (match, respond) in _rules)
        {
            if (match(url))
            {
                return Task.FromResult(respond());
            }
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}

public class OnlineRetryOrderTests
{
    private static OnlineCoverFetcher Fetcher(OrderedMockHandler handler)
        => new(Mock.Of<ILogger<OnlineCoverFetcher>>(), new HttpClient(handler));

    /// <summary>Open Library answers only this exact title, and says nothing to anyone else.</summary>
    private static OrderedMockHandler HandlerFindingOnly(string titleQuery)
    {
        var handler = new OrderedMockHandler();
        // Uri.ToString() hands back a percent-decoded URL, so match the plain text.
        handler.When(
            url => url.Contains("openlibrary.org/search", StringComparison.Ordinal)
                && Uri.UnescapeDataString(url).Contains($"title={titleQuery}&", StringComparison.Ordinal),
            () => OrderedMockHandler.Json(new { docs = new[] { new { title = titleQuery, cover_i = 1234 } } }));
        handler.When(
            url => url.Contains("covers.openlibrary.org", StringComparison.Ordinal),
            () => OrderedMockHandler.Jpeg(5000));
        handler.WhenUrlContains("openlibrary.org/search", new { docs = Array.Empty<object>() });
        handler.WhenUrlContains("googleapis.com/books", new { totalItems = 0 });
        return handler;
    }

    [Fact]
    public async Task FetchCover_NameIsAuthorThenTitle_FoundOnTheSwappedRetry()
    {
        // Parsed as title "Ana Ruiz" / author "Quiet Harbour" — the wrong way round.
        // Only the swapped query names a real book.
        var handler = HandlerFindingOnly("Quiet Harbour");

        var result = await Fetcher(handler).FetchCoverAsync("Ana Ruiz", "Quiet Harbour", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(5000, result!.Value.Stream.Length);

        // The whole sequence, in order. Asserting a count would pass just as happily
        // if a query were skipped, if Google Books were asked before Open Library, or
        // if anything were asked after the hit.
        Assert.Equal(
            [
                "OL title='Ana Ruiz' author='Quiet Harbour'",
                "GB q='intitle:Ana Ruiz+inauthor:Quiet Harbour'",
                "OL title='Ana Ruiz'",
                "GB q='Ana Ruiz'",
                "OL title='Quiet Harbour' author='Ana Ruiz'"
            ],
            handler.LookupSequence);
    }

    [Fact]
    public async Task FetchCover_TitleCarriesASubtitle_FoundOnTheMainTitleRetry()
    {
        var handler = HandlerFindingOnly("Four Thousand Weeks");

        var result = await Fetcher(handler).FetchCoverAsync(
            "Four Thousand Weeks: Time Management for Mortals", null, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(5000, result!.Value.Stream.Length);

        // With no author there is nothing to retry without and nothing to swap, so
        // the main-title query is the third and last thing asked.
        Assert.Equal(
            [
                "OL title='Four Thousand Weeks: Time Management for Mortals'",
                "GB q='Four Thousand Weeks: Time Management for Mortals'",
                "OL title='Four Thousand Weeks'"
            ],
            handler.LookupSequence);
    }

    [Fact]
    public async Task FetchCover_OneWordMainTitle_IsNeverRetried()
    {
        // "Build" alone matches tens of thousands of books; the guard must hold even
        // though a cover is sitting right there for it.
        var handler = HandlerFindingOnly("Build");

        var result = await Fetcher(handler).FetchCoverAsync(
            "Build: An Unorthodox Guide to Making Things", null, CancellationToken.None);

        Assert.Null(result);
        Assert.DoesNotContain(
            handler.RequestedUrls,
            u => Uri.UnescapeDataString(u).Contains("title=Build&", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FetchCover_OnlyGoogleBooksAnswers_TheMainTitleRetryStillUsesIt()
    {
        // Open Library knows nothing at all; Google Books has the main title.
        var handler = new OrderedMockHandler();
        handler.WhenUrlContains("openlibrary.org", new { docs = Array.Empty<object>() });
        handler.When(
            url => url.Contains("googleapis.com/books", StringComparison.Ordinal)
                && Uri.UnescapeDataString(url).Contains("intitle:Four Thousand Weeks+", StringComparison.Ordinal),
            () => OrderedMockHandler.Json(new
            {
                items = new[]
                {
                    new { volumeInfo = new { imageLinks = new { thumbnail = "https://books.google.com/x?zoom=1" } } }
                }
            }));
        handler.When(
            url => url.Contains("books.google.com", StringComparison.Ordinal),
            () => OrderedMockHandler.Jpeg(6000));
        handler.WhenUrlContains("googleapis.com/books", new { totalItems = 0 });

        var result = await Fetcher(handler).FetchCoverAsync(
            "Four Thousand Weeks: Time Management for Mortals", "Oliver Burkeman", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(6000, result!.Value.Stream.Length);
    }

    [Fact]
    public async Task FetchCover_StraightQueryWins_NoRetriesAreMade()
    {
        var handler = HandlerFindingOnly("Quiet Harbour");

        var result = await Fetcher(handler).FetchCoverAsync("Quiet Harbour", "Ana Ruiz", CancellationToken.None);

        Assert.NotNull(result);

        // One lookup and no more: a hit must not be followed by a retry.
        Assert.Equal(["OL title='Quiet Harbour' author='Ana Ruiz'"], handler.LookupSequence);
    }
}
