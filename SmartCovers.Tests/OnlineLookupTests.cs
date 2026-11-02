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
    public async Task LongTitle_WithNoKnownAuthor_IsFoundByFreeTextAlone()
    {
        var handler = Handler(h => h.When(
            url => IsFreeText(url, "La estación de las flores en llamas"),
            () => OrderedMockHandler.Json(Doc("Die Zeit der Feuerblüten", 7374806, "Christiane Gohl"))));

        var result = await Fetcher(handler).FetchCoverAsync("La estación de las flores en llamas", null, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Contains(handler.RequestedUrls, u => u.Contains("/b/id/7374806-L.jpg", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TitleOnlyRetry_StillChecksTheAuthor_OnTheRealOpenLibraryReply()
    {
        // Open Library's real reply to q=Cuando se apagan las luces: the first hit
        // with a cover is another author's book about cinema.
        var reply = new
        {
            docs = new object[]
            {
                new { title = "Cuando se apagan las luces", author_name = new[] { "Pauline Kael" } },
                new { title = "Solos ante el cine", author_name = new[] { "Pedro García Cueto" }, cover_i = 13954412 },
            }
        };
        var handler = Handler(h => h.When(
            url => IsFreeText(url, "Cuando se apagan las luces"),
            () => OrderedMockHandler.Json(reply)));

        var lookup = await Fetcher(handler).LookupAsync(
            new BookQuery("Cuando se apagan las luces", "Care Santos", []), CancellationToken.None);

        Assert.Null(lookup.Cover);
        Assert.False(lookup.NetworkError);
        Assert.DoesNotContain("OL q='Cuando se apagan las luces'", handler.LookupSequence);
        Assert.DoesNotContain(handler.RequestedUrls, u => u.Contains("/b/id/13954412", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PenName_FreeTextHitUnderTheRealName_IsNotTaken()
    {
        // Sarah Lark is Christiane Gohl's pen name, but nothing in the reply says
        // so: taking it would equally take any stranger's book.
        var handler = Handler(h => h.When(
            url => IsFreeText(url, "La estación de las flores en llamas"),
            () => OrderedMockHandler.Json(Doc("Die Zeit der Feuerblüten", 7374806, "Christiane Gohl"))));

        var result = await Fetcher(handler).FetchCoverAsync("La estación de las flores en llamas", "Sarah Lark", CancellationToken.None);

        Assert.Null(result);
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
    public async Task Isbn_IsTriedFirst_ThroughTheSearch_AndTheCoverById()
    {
        var handler = Handler(h => h.When(
            url => url.Contains("search.json?isbn=9780553293357&", StringComparison.Ordinal),
            () => OrderedMockHandler.Json(Doc("Foundation", 14612610, "Isaac Asimov"))));

        var lookup = await Fetcher(handler).LookupAsync(
            new BookQuery("Foundation", "Isaac Asimov", ["9780553293357"]), CancellationToken.None);

        Assert.NotNull(lookup.Cover);
        Assert.Equal("Open Library", lookup.Source);
        Assert.Equal(2, handler.RequestedUrls.Count);
        Assert.Contains("/b/id/14612610-L.jpg?default=false", handler.RequestedUrls[1], StringComparison.Ordinal);

        // The covers host's per-address ISBN limit is never touched.
        Assert.DoesNotContain(handler.RequestedUrls, u => u.Contains("/b/isbn/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Oclc_IsSearchedTheSameWay()
    {
        var handler = Handler(h => h.When(
            url => url.Contains("search.json?oclc=15680789&", StringComparison.Ordinal),
            () => OrderedMockHandler.Json(Doc("Catálogo de privilegios", 777, "Alcocer"))));

        var lookup = await Fetcher(handler).LookupAsync(
            new BookQuery("Cátalogo de privilegios y mercedes de hidalguía", null, [], "15680789"), CancellationToken.None);

        Assert.NotNull(lookup.Cover);
        Assert.DoesNotContain(handler.RequestedUrls, u => u.Contains("/b/oclc/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Isbn_WithNoCover_404_FallsThroughToTheTitleSearch()
    {
        var handler = Handler(_ => { });

        var lookup = await Fetcher(handler).LookupAsync(
            new BookQuery("Ordenanzas municipales de la Ribera del Júcar", null, ["9788487136023"]), CancellationToken.None);

        Assert.Null(lookup.Cover);
        Assert.False(lookup.NetworkError);
        Assert.Contains(handler.RequestedUrls, u => u.Contains("search.json?isbn=9788487136023&", StringComparison.Ordinal));
        Assert.Contains(handler.RequestedUrls, u => u.Contains("search.json?title=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OpenLibraryPlaceholderGif_IsNotACover()
    {
        var handler = new OrderedMockHandler();
        handler.When(
            url => url.Contains("search.json?isbn=", StringComparison.Ordinal),
            () => OrderedMockHandler.Json(Doc("x", 99, "Nobody")));
        handler.When(
            url => url.Contains("/b/id/99-", StringComparison.Ordinal),
            () =>
            {
                // The 43-byte 1x1 GIF Open Library serves without default=false,
                // padded so only the content type can reject it.
                var gif = new byte[2000];
                "GIF89a"u8.ToArray().CopyTo(gif, 0);
                var msg = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(gif) };
                msg.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/gif");
                return msg;
            });
        handler.WhenUrlContains("openlibrary.org/search", new { docs = Array.Empty<object>() });
        handler.WhenUrlContains("googleapis.com/books", new { totalItems = 0 });

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

    [Fact]
    public async Task OpenLibraryRefusal_StopsTheLookup_AndPausesItAcrossItems()
    {
        var logger = new Mock<ILogger<OnlineCoverFetcher>>();
        var handler = new OrderedMockHandler();
        handler.When(url => url.Contains("openlibrary.org", StringComparison.Ordinal), () => new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        handler.WhenUrlContains("googleapis.com/books", new { totalItems = 0 });
        var fetcher = Fetcher(handler, logger.Object);

        var first = await fetcher.LookupAsync(new BookQuery("Hacia la Fundación", "Isaac Asimov", ["9780553293357"]), CancellationToken.None);
        var second = await fetcher.LookupAsync(new BookQuery("Distant Shore", "Bo Lindqvist", []), CancellationToken.None);

        // One refused request in all, not one per reading and per item.
        Assert.Single(handler.RequestedUrls, u => u.Contains("openlibrary.org", StringComparison.Ordinal));
        Assert.True(first.NetworkError);
        Assert.Contains("429", first.Detail, StringComparison.Ordinal);
        Assert.True(second.NetworkError);
        Assert.Contains("paused", second.Detail, StringComparison.Ordinal);

        logger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => $"{v}".Contains("Open Library refused", StringComparison.Ordinal)),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task OpenLibraryPlainMiss_404_DoesNotPause()
    {
        var handler = Handler(_ => { });
        var fetcher = Fetcher(handler);

        await fetcher.LookupAsync(new BookQuery("Quiet Harbour", null, ["9788487136023"]), CancellationToken.None);
        var before = handler.RequestedUrls.Count(u => u.Contains("openlibrary.org", StringComparison.Ordinal));
        var second = await fetcher.LookupAsync(new BookQuery("Distant Shore", null, []), CancellationToken.None);

        Assert.False(second.NetworkError);
        Assert.True(handler.RequestedUrls.Count(u => u.Contains("openlibrary.org", StringComparison.Ordinal)) > before);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "{\"error\":{\"code\":400,\"message\":\"API key not valid. Please pass a valid API key.\",\"status\":\"INVALID_ARGUMENT\"}}", "API key rejected")]
    [InlineData(HttpStatusCode.Forbidden, "{\"error\":{\"code\":403,\"message\":\"Books API has not been used in project 1 before or it is disabled.\",\"status\":\"PERMISSION_DENIED\"}}", "API key rejected")]
    [InlineData(HttpStatusCode.Forbidden, "{\"error\":{\"code\":403,\"errors\":[{\"reason\":\"dailyLimitExceeded\"}]}}", "quota exhausted")]
    [InlineData(HttpStatusCode.TooManyRequests, "{\"error\":{\"status\":\"RESOURCE_EXHAUSTED\"}}", "quota exhausted")]
    public async Task GoogleBooksRefusingAKey_SaysWhy_AndPauses(HttpStatusCode status, string body, string expected)
    {
        var logger = new Mock<ILogger<OnlineCoverFetcher>>();
        var handler = new OrderedMockHandler();
        handler.When(
            url => url.Contains("googleapis.com", StringComparison.Ordinal),
            () => new HttpResponseMessage(status) { Content = new StringContent(body) });
        handler.WhenUrlContains("openlibrary.org/search", new { docs = Array.Empty<object>() });
        var fetcher = new OnlineCoverFetcher(logger.Object, new HttpClient(handler)) { GoogleBooksApiKeySource = () => "bad-key" };

        var first = await fetcher.LookupAsync(new BookQuery("Quiet Harbour", "Ana Ruiz", []), CancellationToken.None);
        await fetcher.LookupAsync(new BookQuery("Distant Shore", "Bo Lindqvist", []), CancellationToken.None);

        Assert.Single(handler.RequestedUrls, u => u.Contains("googleapis.com", StringComparison.Ordinal));
        Assert.False(first.NetworkError);
        Assert.Contains(expected, first.Detail, StringComparison.Ordinal);

        var warning = expected == "API key rejected" ? "rejected the API key" : "quota is exhausted";
        logger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => $"{v}".Contains(warning, StringComparison.Ordinal)),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task GoogleBooks400_WithoutAKey_IsAPlainFailure_NotAPause()
    {
        var handler = new OrderedMockHandler();
        handler.When(url => url.Contains("googleapis.com", StringComparison.Ordinal), () => new HttpResponseMessage(HttpStatusCode.BadRequest));
        handler.WhenUrlContains("openlibrary.org/search", new { docs = Array.Empty<object>() });
        var fetcher = Fetcher(handler);

        await fetcher.LookupAsync(new BookQuery("Quiet Harbour", "Ana Ruiz", []), CancellationToken.None);
        await fetcher.LookupAsync(new BookQuery("Distant Shore", "Bo Lindqvist", []), CancellationToken.None);

        Assert.True(handler.RequestedUrls.Count(u => u.Contains("googleapis.com", StringComparison.Ordinal)) > 1);
    }

    private static HttpResponseMessage NotJson() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("<html>Service Unavailable</html>")
    };

    [Fact]
    public async Task OpenLibraryUnreadableReply_IsANetworkError_SoTheNextRunTriesAgain()
    {
        var handler = new OrderedMockHandler();
        handler.When(url => url.Contains("openlibrary.org/search", StringComparison.Ordinal), NotJson);
        handler.WhenUrlContains("googleapis.com/books", new { totalItems = 0 });

        var lookup = await Fetcher(handler).LookupAsync(new BookQuery("Archivística en Navarra", null, []), CancellationToken.None);

        Assert.Null(lookup.Cover);
        Assert.True(lookup.NetworkError);
        Assert.Contains("Open Library: unreadable reply", lookup.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GoogleBooksUnreadableReply_IsAMiss_NotACrash()
    {
        var handler = new OrderedMockHandler();
        handler.When(url => url.Contains("googleapis.com/books", StringComparison.Ordinal), NotJson);
        handler.WhenUrlContains("openlibrary.org/search", new { docs = Array.Empty<object>() });

        var lookup = await Fetcher(handler).LookupAsync(new BookQuery("Archivística en Navarra", null, []), CancellationToken.None);

        Assert.Null(lookup.Cover);
        Assert.Contains(handler.RequestedUrls, u => u.Contains("googleapis.com/books", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OpenLibraryTimeout_IsANetworkError()
    {
        var handler = new OrderedMockHandler();
        handler.When(url => url.Contains("openlibrary.org", StringComparison.Ordinal), () => throw new TaskCanceledException("timeout"));
        handler.WhenUrlContains("googleapis.com/books", new { totalItems = 0 });

        var lookup = await Fetcher(handler).LookupAsync(new BookQuery("Archivística en Navarra", null, []), CancellationToken.None);

        Assert.True(lookup.NetworkError);
        Assert.Contains("Open Library: timed out", lookup.Detail, StringComparison.Ordinal);
    }
}
