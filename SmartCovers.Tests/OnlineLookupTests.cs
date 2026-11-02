using System.Net;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace SmartCovers.Tests;

/// <summary>
/// The lookup changes behind real misses: Open Library's free-text search for
/// translated titles, ISBN first, and failures that say why.
/// </summary>
public class OnlineLookupTests
{
    private static OnlineCoverFetcher Fetcher(OrderedMockHandler handler, ILogger<OnlineCoverFetcher>? logger = null)
        => new(logger ?? Mock.Of<ILogger<OnlineCoverFetcher>>(), new HttpClient(handler))
        {
            GoogleBooksApiKeySource = () => null
        };

    private static bool IsFreeText(string url, string text)
        => url.Contains("openlibrary.org/search", StringComparison.Ordinal)
            && Uri.UnescapeDataString(url).Contains($"q={text}&", StringComparison.Ordinal);

    private static OrderedMockHandler Handler(Action<OrderedMockHandler> rules)
    {
        var handler = new OrderedMockHandler();
        rules(handler);
        handler.When(url => url.Contains("covers.openlibrary.org/b/id/", StringComparison.Ordinal), () => OrderedMockHandler.Jpeg(5000));
        handler.WhenUrlContains("openlibrary.org/search", new { docs = Array.Empty<object>() });
        handler.WhenUrlContains("googleapis.com/books", new { totalItems = 0 });
        return handler;
    }

    private static object Doc(string title, int cover, params string[] authors)
        => new { docs = new[] { new { title, author_name = authors, cover_i = cover } } };

    [Fact]
    public async Task SpanishTitle_FoundByFreeTextSearch_WithTheAuthorChecked()
    {
        // title=/author= finds nothing for the Spanish title; q= finds the English work.
        var handler = Handler(h => h.When(
            url => IsFreeText(url, "Hacia la Fundación Isaac Asimov"),
            () => OrderedMockHandler.Json(Doc("Forward the Foundation", 9300894, "Isaac Asimov"))));

        var result = await Fetcher(handler).FetchCoverAsync("Hacia la Fundación", "Isaac Asimov", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Contains(handler.RequestedUrls, u => u.Contains("covers.openlibrary.org/b/id/9300894-L.jpg?default=false", StringComparison.Ordinal));
        Assert.Equal(
            ["OL title='Hacia la Fundación' author='Isaac Asimov'", "OL q='Hacia la Fundación Isaac Asimov'"],
            handler.LookupSequence);
    }

    [Fact]
    public async Task FreeTextHit_ByAnotherAuthor_IsRejected()
    {
        // A stranger's book with a similar title, and no author at all.
        var handler = Handler(h => h.When(
            url => url.Contains("openlibrary.org/search", StringComparison.Ordinal) && Uri.UnescapeDataString(url).Contains("q=", StringComparison.Ordinal),
            () => OrderedMockHandler.Json(new { docs = new[] { new { title = "Hacia un estatuto de las fundaciones en España", cover_i = 1 } } })));

        var result = await Fetcher(handler).FetchCoverAsync("Hacia la Fundación", "Isaac Asimov", CancellationToken.None);

        Assert.Null(result);
        Assert.DoesNotContain(handler.RequestedUrls, u => u.Contains("/b/id/1-L.jpg", StringComparison.Ordinal));

        // The title alone is too short to search unchecked.
        Assert.DoesNotContain("OL q='Hacia la Fundación'", handler.LookupSequence);
    }

    [Fact]
    public async Task LongTitle_FoundByFreeTextAlone_UnderTheAuthorsRealName()
    {
        // Sarah Lark is a pen name; Open Library lists the book as Christiane Gohl's.
        var handler = Handler(h => h.When(
            url => IsFreeText(url, "La estación de las flores en llamas"),
            () => OrderedMockHandler.Json(Doc("Die Zeit der Feuerblüten", 7374806, "Christiane Gohl"))));

        var result = await Fetcher(handler).FetchCoverAsync("La estación de las flores en llamas", "Sarah Lark", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Contains(handler.RequestedUrls, u => u.Contains("/b/id/7374806-L.jpg", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AuthorListFolder_FoundWithTheFirstAuthor()
    {
        var (title, author) = OnlineCoverFetcher.ParseBookInfo(
            new MediaBrowser.Controller.Entities.Book { Name = "01" },
            "Douglas Stone, Bruce Patton, and Sheila Heen - Difficult Conversations");

        var handler = Handler(h => h.When(
            url => IsFreeText(url, "Difficult Conversations Douglas Stone"),
            () => OrderedMockHandler.Json(Doc("Difficult Conversations", 2555094, "Douglas Stone", "Bruce Patton", "Sheila Heen"))));

        var result = await Fetcher(handler).FetchCoverAsync(title, author, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Contains(handler.RequestedUrls, u => u.Contains("/b/id/2555094-L.jpg", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OneWordMainTitle_FoundOnlyThroughTheWholeTitle()
    {
        Assert.Null(OnlineCoverFetcher.MainTitleOf("Build, An Unorthodox Guide to Making Things Worth Making"));

        var handler = Handler(h => h.When(
            url => IsFreeText(url, "Build, An Unorthodox Guide to Making Things Worth Making"),
            () => OrderedMockHandler.Json(Doc("Build", 12742378, "Tony Fadell"))));

        var result = await Fetcher(handler).FetchCoverAsync(
            "Build, An Unorthodox Guide to Making Things Worth Making", null, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Contains(handler.RequestedUrls, u => u.Contains("/b/id/12742378-L.jpg", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.RequestedUrls, u => Uri.UnescapeDataString(u).Contains("title=Build&", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Isbn_IsTriedFirst_AndAHitNeedsNoSearch()
    {
        var handler = Handler(h => h.When(
            url => url.Contains("covers.openlibrary.org/b/isbn/9780553293357-L.jpg?default=false", StringComparison.Ordinal),
            () => OrderedMockHandler.Jpeg(39759)));

        var lookup = await Fetcher(handler).LookupAsync(
            new BookQuery("Foundation", "Isaac Asimov", ["9780553293357"]), CancellationToken.None);

        Assert.NotNull(lookup.Cover);
        Assert.Equal("Open Library", lookup.Source);
        Assert.DoesNotContain(handler.RequestedUrls, u => u.Contains("search.json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Isbn_WithNoCover_404_FallsThroughToTheTitleSearch()
    {
        var handler = Handler(_ => { });

        var lookup = await Fetcher(handler).LookupAsync(
            new BookQuery("Ordenanzas municipales de la Ribera del Júcar", null, ["9788487136023"]), CancellationToken.None);

        Assert.Null(lookup.Cover);
        Assert.False(lookup.NetworkError);
        Assert.Contains(handler.RequestedUrls, u => u.Contains("/b/isbn/9788487136023-L.jpg?default=false", StringComparison.Ordinal));
        Assert.Contains(handler.RequestedUrls, u => u.Contains("search.json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OpenLibraryPlaceholderGif_IsNotACover()
    {
        var handler = Handler(h => h.When(
            url => url.Contains("/b/isbn/", StringComparison.Ordinal),
            () =>
            {
                // The 43-byte 1x1 GIF Open Library serves without default=false,
                // padded so only the content type can reject it.
                var gif = new byte[2000];
                "GIF89a"u8.ToArray().CopyTo(gif, 0);
                var msg = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(gif) };
                msg.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/gif");
                return msg;
            }));

        var lookup = await Fetcher(handler).LookupAsync(new BookQuery("x", null, ["9788487136023"]), CancellationToken.None);

        Assert.Null(lookup.Cover);
    }

    [Fact]
    public async Task GoogleBooks429_PausesGoogleForTheRun_AndWarnsOnce()
    {
        var logger = new Mock<ILogger<OnlineCoverFetcher>>();
        var handler = new OrderedMockHandler();
        handler.When(
            url => url.Contains("googleapis.com", StringComparison.Ordinal),
            () => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("{\"error\":{\"status\":\"RESOURCE_EXHAUSTED\"}}")
            });
        handler.WhenUrlContains("openlibrary.org/search", new { docs = Array.Empty<object>() });
        var fetcher = Fetcher(handler, logger.Object);

        var first = await fetcher.LookupAsync(new BookQuery("Quiet Harbour", "Ana Ruiz", []), CancellationToken.None);
        var second = await fetcher.LookupAsync(new BookQuery("Distant Shore", "Bo Lindqvist", []), CancellationToken.None);

        Assert.Single(handler.RequestedUrls, u => u.Contains("googleapis.com", StringComparison.Ordinal));

        // Open Library did answer, so this is still a real miss: a drawn cover may follow.
        Assert.False(first.NetworkError);
        Assert.Contains("Google Books", first.Detail, StringComparison.Ordinal);
        Assert.Contains("quota", second.Detail, StringComparison.Ordinal);

        logger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => $"{v}".Contains("Google Books", StringComparison.Ordinal)),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task GoogleBooksKey_IsSentWhenSet_AndANewKeyLiftsThePause()
    {
        var handler = new OrderedMockHandler();
        handler.When(
            url => url.Contains("googleapis.com", StringComparison.Ordinal) && !url.Contains("key=", StringComparison.Ordinal),
            () => new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        handler.WhenUrlContains("googleapis.com/books", new { totalItems = 0 });
        handler.WhenUrlContains("openlibrary.org/search", new { docs = Array.Empty<object>() });

        string? key = null;
        var fetcher = new OnlineCoverFetcher(Mock.Of<ILogger<OnlineCoverFetcher>>(), new HttpClient(handler))
        {
            GoogleBooksApiKeySource = () => key
        };

        await fetcher.LookupAsync(new BookQuery("Quiet Harbour", null, []), CancellationToken.None);
        key = "my-key";
        await fetcher.LookupAsync(new BookQuery("Distant Shore", null, []), CancellationToken.None);

        Assert.Contains(handler.RequestedUrls, u => u.Contains("googleapis.com", StringComparison.Ordinal) && u.Contains("key=my-key", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, "429")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "503")]
    public async Task OpenLibraryRefusing_IsANetworkError_NotANoMatch(HttpStatusCode status, string expected)
    {
        var handler = new OrderedMockHandler();
        handler.When(url => url.Contains("openlibrary.org", StringComparison.Ordinal), () => new HttpResponseMessage(status));
        handler.WhenUrlContains("googleapis.com/books", new { totalItems = 0 });

        var lookup = await Fetcher(handler).LookupAsync(new BookQuery("Archivística en Navarra", null, []), CancellationToken.None);

        Assert.Null(lookup.Cover);
        Assert.True(lookup.NetworkError);
        Assert.Contains(expected, lookup.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenLibraryUnreachable_IsANetworkError()
    {
        var handler = new OrderedMockHandler();
        handler.When(url => url.Contains("openlibrary.org", StringComparison.Ordinal), () => throw new HttpRequestException("Name or service not known"));
        handler.WhenUrlContains("googleapis.com/books", new { totalItems = 0 });

        var lookup = await Fetcher(handler).LookupAsync(new BookQuery("Archivística en Navarra", null, []), CancellationToken.None);

        Assert.True(lookup.NetworkError);
        Assert.Contains("Open Library", lookup.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoDocs_IsAPlainMiss()
    {
        var handler = Handler(_ => { });

        var lookup = await Fetcher(handler).LookupAsync(new BookQuery("Archivística en Navarra", null, []), CancellationToken.None);

        Assert.Null(lookup.Cover);
        Assert.False(lookup.NetworkError);
        Assert.Null(lookup.Detail);
    }
}
