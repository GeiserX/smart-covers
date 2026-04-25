using System.Net;
using System.Text;
using System.Text.Json;
using Moq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Drawing;
using Microsoft.Extensions.Logging;
using Xunit;

namespace SmartCovers.Tests;

/// <summary>
/// A mock HttpMessageHandler that returns canned responses based on URL patterns.
/// </summary>
internal class MockHttpHandler : HttpMessageHandler
{
    private readonly Dictionary<string, (HttpStatusCode Status, byte[] Content, string ContentType)> _responses = new();
    private readonly List<string> _requestedUrls = new();

    public IReadOnlyList<string> RequestedUrls => _requestedUrls;

    public void AddResponse(string urlContains, HttpStatusCode status, byte[] content, string contentType = "application/json")
    {
        _responses[urlContains] = (status, content, contentType);
    }

    public void AddJsonResponse(string urlContains, object jsonObj)
    {
        var json = JsonSerializer.Serialize(jsonObj);
        AddResponse(urlContains, HttpStatusCode.OK, Encoding.UTF8.GetBytes(json));
    }

    public void AddImageResponse(string urlContains, byte[] imageData)
    {
        AddResponse(urlContains, HttpStatusCode.OK, imageData, "image/jpeg");
    }

    public void AddNotFound(string urlContains)
    {
        AddResponse(urlContains, HttpStatusCode.NotFound, Array.Empty<byte>());
    }

    public void AddError(string urlContains)
    {
        AddResponse(urlContains, HttpStatusCode.InternalServerError, Array.Empty<byte>());
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri?.ToString() ?? "";
        _requestedUrls.Add(url);

        foreach (var (pattern, response) in _responses)
        {
            if (url.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                var msg = new HttpResponseMessage(response.Status)
                {
                    Content = new ByteArrayContent(response.Content)
                };
                msg.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(response.ContentType);
                return Task.FromResult(msg);
            }
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}

public class HttpFetchTests
{
    private static byte[] CreateFakeJpeg(int size = 5000)
    {
        var data = new byte[size];
        data[0] = 0xFF; data[1] = 0xD8; data[2] = 0xFF; data[3] = 0xE0;
        return data;
    }

    private static byte[] CreateFakePng(int size = 5000)
    {
        var data = new byte[size];
        data[0] = 0x89; data[1] = 0x50; data[2] = 0x4E; data[3] = 0x47;
        return data;
    }

    private static (OnlineCoverFetcher Fetcher, MockHttpHandler Handler) CreateFetcherWithMockHttp()
    {
        var handler = new MockHttpHandler();
        var client = new HttpClient(handler);
        var fetcher = new OnlineCoverFetcher(Mock.Of<ILogger<OnlineCoverFetcher>>(), client);
        return (fetcher, handler);
    }

    // --- Open Library tests ---

    [Fact]
    public async Task TryOpenLibrary_WithCover_ReturnsImage()
    {
        var (fetcher, handler) = CreateFetcherWithMockHttp();

        // Open Library search returns a doc with cover_i
        handler.AddJsonResponse("openlibrary.org/search.json", new
        {
            docs = new[]
            {
                new { title = "Test Book", cover_i = 12345 }
            }
        });

        // Cover image endpoint returns a valid JPEG
        handler.AddImageResponse("covers.openlibrary.org", CreateFakeJpeg());

        var result = await fetcher.FetchCoverAsync("Test Book", "Author", CancellationToken.None);
        Assert.NotNull(result);
        Assert.Equal(ImageFormat.Jpg, result.Value.Format);
        result.Value.Stream.Dispose();
    }

    [Fact]
    public async Task TryOpenLibrary_NoCoverId_ReturnsNull()
    {
        var (fetcher, handler) = CreateFetcherWithMockHttp();

        handler.AddJsonResponse("openlibrary.org/search.json", new
        {
            docs = new[]
            {
                new { title = "Test Book" }
            }
        });

        // Google Books also returns nothing
        handler.AddJsonResponse("googleapis.com", new { totalItems = 0 });

        var result = await fetcher.FetchCoverAsync("Test Book", null, CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task TryOpenLibrary_EmptyDocs_FallsToGoogle()
    {
        var (fetcher, handler) = CreateFetcherWithMockHttp();

        handler.AddJsonResponse("openlibrary.org/search.json", new { docs = Array.Empty<object>() });

        handler.AddJsonResponse("googleapis.com/books", new
        {
            items = new[]
            {
                new
                {
                    volumeInfo = new
                    {
                        imageLinks = new { thumbnail = "http://books.google.com/books?id=test&zoom=1" }
                    }
                }
            }
        });

        handler.AddImageResponse("books.google.com", CreateFakeJpeg());

        var result = await fetcher.FetchCoverAsync("Test Book", "Author", CancellationToken.None);
        Assert.NotNull(result);
        result!.Value.Stream.Dispose();
    }

    [Fact]
    public async Task TryOpenLibrary_NoDocs_ReturnsNull()
    {
        var (fetcher, handler) = CreateFetcherWithMockHttp();

        // Response without "docs" property
        handler.AddJsonResponse("openlibrary.org/search.json", new { numFound = 0 });
        handler.AddJsonResponse("googleapis.com", new { totalItems = 0 });

        var result = await fetcher.FetchCoverAsync("Test Book", null, CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task TryOpenLibrary_ServerError_FallsToGoogle()
    {
        var (fetcher, handler) = CreateFetcherWithMockHttp();

        handler.AddError("openlibrary.org");
        handler.AddJsonResponse("googleapis.com/books", new
        {
            items = new[]
            {
                new
                {
                    volumeInfo = new
                    {
                        imageLinks = new { thumbnail = "http://books.google.com/books?id=test&zoom=1" }
                    }
                }
            }
        });
        handler.AddImageResponse("books.google.com", CreateFakeJpeg());

        var result = await fetcher.FetchCoverAsync("Test Book", "Author", CancellationToken.None);
        Assert.NotNull(result);
        result!.Value.Stream.Dispose();
    }

    [Fact]
    public async Task TryOpenLibrary_CoverImageTooSmall_FallsToGoogle()
    {
        var (fetcher, handler) = CreateFetcherWithMockHttp();

        handler.AddJsonResponse("openlibrary.org/search.json", new
        {
            docs = new[]
            {
                new { title = "Test Book", cover_i = 12345 }
            }
        });

        // Cover image is too small (placeholder)
        handler.AddImageResponse("covers.openlibrary.org", new byte[100]);

        handler.AddJsonResponse("googleapis.com", new { totalItems = 0 });

        var result = await fetcher.FetchCoverAsync("Test Book", null, CancellationToken.None);
        Assert.Null(result);
    }

    // --- Google Books tests ---

    [Fact]
    public async Task TryGoogleBooks_WithLargeImage_ReturnsImage()
    {
        var (fetcher, handler) = CreateFetcherWithMockHttp();

        handler.AddJsonResponse("openlibrary.org/search.json", new { docs = Array.Empty<object>() });

        handler.AddJsonResponse("googleapis.com/books", new
        {
            items = new[]
            {
                new
                {
                    volumeInfo = new
                    {
                        imageLinks = new { large = "https://books.google.com/large?zoom=1" }
                    }
                }
            }
        });

        handler.AddImageResponse("books.google.com", CreateFakeJpeg());

        var result = await fetcher.FetchCoverAsync("Test Book", "Author", CancellationToken.None);
        Assert.NotNull(result);
        result!.Value.Stream.Dispose();
    }

    [Fact]
    public async Task TryGoogleBooks_WithMediumImage_ReturnsImage()
    {
        var (fetcher, handler) = CreateFetcherWithMockHttp();

        handler.AddJsonResponse("openlibrary.org/search.json", new { docs = Array.Empty<object>() });

        handler.AddJsonResponse("googleapis.com/books", new
        {
            items = new[]
            {
                new
                {
                    volumeInfo = new
                    {
                        imageLinks = new { medium = "https://books.google.com/medium?zoom=5&edge=curl" }
                    }
                }
            }
        });

        handler.AddImageResponse("books.google.com", CreateFakePng());

        var result = await fetcher.FetchCoverAsync("Test Book", "Author", CancellationToken.None);
        Assert.NotNull(result);
        Assert.Equal(ImageFormat.Png, result!.Value.Format);
        result.Value.Stream.Dispose();
    }

    [Fact]
    public async Task TryGoogleBooks_WithSmallThumbnailOnly_ReturnsImage()
    {
        var (fetcher, handler) = CreateFetcherWithMockHttp();

        handler.AddJsonResponse("openlibrary.org/search.json", new { docs = Array.Empty<object>() });

        handler.AddJsonResponse("googleapis.com/books", new
        {
            items = new[]
            {
                new
                {
                    volumeInfo = new
                    {
                        imageLinks = new { smallThumbnail = "http://books.google.com/small?zoom=1" }
                    }
                }
            }
        });

        handler.AddImageResponse("books.google.com", CreateFakeJpeg());

        var result = await fetcher.FetchCoverAsync("Test Book", "Author", CancellationToken.None);
        Assert.NotNull(result);
        result!.Value.Stream.Dispose();
    }

    [Fact]
    public async Task TryGoogleBooks_NoItems_ReturnsNull()
    {
        var (fetcher, handler) = CreateFetcherWithMockHttp();

        handler.AddJsonResponse("openlibrary.org/search.json", new { docs = Array.Empty<object>() });
        handler.AddJsonResponse("googleapis.com", new { totalItems = 0 });

        var result = await fetcher.FetchCoverAsync("Test Book", null, CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task TryGoogleBooks_NoImageLinks_ReturnsNull()
    {
        var (fetcher, handler) = CreateFetcherWithMockHttp();

        handler.AddJsonResponse("openlibrary.org/search.json", new { docs = Array.Empty<object>() });

        handler.AddJsonResponse("googleapis.com/books", new
        {
            items = new[]
            {
                new
                {
                    volumeInfo = new
                    {
                        title = "Test Book"
                        // No imageLinks
                    }
                }
            }
        });

        var result = await fetcher.FetchCoverAsync("Test Book", null, CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task TryGoogleBooks_ServerError_ReturnsNull()
    {
        var (fetcher, handler) = CreateFetcherWithMockHttp();

        handler.AddJsonResponse("openlibrary.org/search.json", new { docs = Array.Empty<object>() });
        handler.AddError("googleapis.com");

        var result = await fetcher.FetchCoverAsync("Test Book", null, CancellationToken.None);
        Assert.Null(result);
    }

    // --- FetchCoverAsync retry logic ---

    [Fact]
    public async Task FetchCoverAsync_WithAuthor_RetriesWithoutAuthor()
    {
        var (fetcher, handler) = CreateFetcherWithMockHttp();

        // First 2 tries (OL + Google with author) return nothing
        // Then retries without author succeed on Open Library
        var callCount = 0;
        // All OL calls return no cover initially, but the 3rd call (without author) returns a cover
        handler.AddJsonResponse("openlibrary.org/search.json", new
        {
            docs = new[]
            {
                new { title = "Test Book", cover_i = 99999 }
            }
        });

        handler.AddImageResponse("covers.openlibrary.org", CreateFakeJpeg());

        var result = await fetcher.FetchCoverAsync("Test Book", "Author", CancellationToken.None);
        // Should succeed on first OL call (with author)
        Assert.NotNull(result);
        result!.Value.Stream.Dispose();
    }

    [Fact]
    public async Task FetchCoverAsync_Cancelled_Throws()
    {
        var (fetcher, handler) = CreateFetcherWithMockHttp();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fetcher.FetchCoverAsync("Test Book", null, cts.Token));
    }

    // --- FetchCoverByOriginalTitleAsync ---

    [Fact]
    public async Task FetchCoverByOriginalTitleAsync_EmptyOriginalTitle_ReturnsNull()
    {
        var (fetcher, _) = CreateFetcherWithMockHttp();
        var item = new Mock<Book>();
        item.SetupGet(i => i.Name).Returns("Test");

        // OriginalTitle is null by default on Book mock
        var result = await fetcher.FetchCoverByOriginalTitleAsync(item.Object, CancellationToken.None);
        Assert.Null(result);
    }

    // --- URL manipulation tests ---

    [Fact]
    public async Task TryGoogleBooks_HttpUrlUpgradedToHttps()
    {
        var (fetcher, handler) = CreateFetcherWithMockHttp();

        handler.AddJsonResponse("openlibrary.org/search.json", new { docs = Array.Empty<object>() });

        handler.AddJsonResponse("googleapis.com/books", new
        {
            items = new[]
            {
                new
                {
                    volumeInfo = new
                    {
                        imageLinks = new { thumbnail = "http://books.google.com/books?id=test&zoom=1&edge=curl" }
                    }
                }
            }
        });

        handler.AddImageResponse("books.google.com", CreateFakeJpeg());

        var result = await fetcher.FetchCoverAsync("Test Book", "Author", CancellationToken.None);
        Assert.NotNull(result);

        // Verify the URL was upgraded to HTTPS and zoom/edge modified
        var googleUrls = handler.RequestedUrls.Where(u => u.Contains("books.google.com")).ToList();
        Assert.True(googleUrls.All(u => u.StartsWith("https://")));
        Assert.True(googleUrls.All(u => u.Contains("zoom=0")));
        Assert.True(googleUrls.All(u => !u.Contains("edge=curl")));

        result!.Value.Stream.Dispose();
    }

    [Fact]
    public async Task TryGoogleBooks_EmptyImageLinks_ReturnsNull()
    {
        var (fetcher, handler) = CreateFetcherWithMockHttp();

        handler.AddJsonResponse("openlibrary.org/search.json", new { docs = Array.Empty<object>() });

        // volumeInfo exists but imageLinks has no recognized keys
        handler.AddJsonResponse("googleapis.com/books", new
        {
            items = new[]
            {
                new
                {
                    volumeInfo = new
                    {
                        imageLinks = new { extraSmall = "http://example.com/tiny.jpg" }
                    }
                }
            }
        });

        var result = await fetcher.FetchCoverAsync("Test Book", null, CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task TryGoogleBooks_ImageUnrecognizedFormat_ReturnsNull()
    {
        var (fetcher, handler) = CreateFetcherWithMockHttp();

        handler.AddJsonResponse("openlibrary.org/search.json", new { docs = Array.Empty<object>() });

        handler.AddJsonResponse("googleapis.com/books", new
        {
            items = new[]
            {
                new
                {
                    volumeInfo = new
                    {
                        imageLinks = new { thumbnail = "https://books.google.com/books?id=test&zoom=1" }
                    }
                }
            }
        });

        // Image with unrecognized format
        var badImage = new byte[5000];
        badImage[0] = 0x01; badImage[1] = 0x02;
        handler.AddImageResponse("books.google.com", badImage);

        var result = await fetcher.FetchCoverAsync("Test Book", null, CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task TryOpenLibrary_WithAuthorInQuery_IncludesAuthorParam()
    {
        var (fetcher, handler) = CreateFetcherWithMockHttp();

        handler.AddJsonResponse("openlibrary.org/search.json", new
        {
            docs = new[]
            {
                new { title = "Book", cover_i = 111 }
            }
        });

        handler.AddImageResponse("covers.openlibrary.org", CreateFakeJpeg());

        var result = await fetcher.FetchCoverAsync("Book", "Author Name", CancellationToken.None);
        Assert.NotNull(result);

        var olUrls = handler.RequestedUrls.Where(u => u.Contains("openlibrary.org/search")).ToList();
        Assert.True(olUrls.Any(u => u.Contains("author=")));

        result!.Value.Stream.Dispose();
    }

    [Fact]
    public async Task TryOpenLibrary_WithoutAuthor_OmitsAuthorParam()
    {
        var (fetcher, handler) = CreateFetcherWithMockHttp();

        handler.AddJsonResponse("openlibrary.org/search.json", new
        {
            docs = new[]
            {
                new { title = "Book", cover_i = 222 }
            }
        });

        handler.AddImageResponse("covers.openlibrary.org", CreateFakeJpeg());

        var result = await fetcher.FetchCoverAsync("Book", null, CancellationToken.None);
        Assert.NotNull(result);

        var olUrls = handler.RequestedUrls.Where(u => u.Contains("openlibrary.org/search")).ToList();
        Assert.True(olUrls.All(u => !u.Contains("author=")));

        result!.Value.Stream.Dispose();
    }

    [Fact]
    public async Task TryGoogleBooks_WithAuthor_UsesInauthorOperator()
    {
        var (fetcher, handler) = CreateFetcherWithMockHttp();

        // OL fails
        handler.AddJsonResponse("openlibrary.org/search.json", new { docs = Array.Empty<object>() });

        handler.AddJsonResponse("googleapis.com/books", new
        {
            items = new[]
            {
                new
                {
                    volumeInfo = new
                    {
                        imageLinks = new { thumbnail = "https://books.google.com/img?zoom=1" }
                    }
                }
            }
        });

        handler.AddImageResponse("books.google.com", CreateFakeJpeg());

        var result = await fetcher.FetchCoverAsync("Book", "Author", CancellationToken.None);
        Assert.NotNull(result);

        var googleUrls = handler.RequestedUrls.Where(u => u.Contains("googleapis.com/books")).ToList();
        Assert.True(googleUrls.Any(u => u.Contains("inauthor%3A") || u.Contains("inauthor:")));

        result!.Value.Stream.Dispose();
    }

    [Fact]
    public async Task FetchCoverAsync_NoBooksAnywhere_AllSourcesExhausted_ReturnsNull()
    {
        var (fetcher, handler) = CreateFetcherWithMockHttp();

        handler.AddJsonResponse("openlibrary.org/search.json", new { docs = Array.Empty<object>() });
        handler.AddJsonResponse("googleapis.com", new { totalItems = 0 });

        var result = await fetcher.FetchCoverAsync("Very Obscure Book", "Unknown Author", CancellationToken.None);
        Assert.Null(result);

        // Should have tried: OL+author, Google+author, OL no-author, Google no-author = 4 searches
        var searchUrls = handler.RequestedUrls.Where(u =>
            u.Contains("openlibrary.org/search") || u.Contains("googleapis.com/books")).ToList();
        Assert.Equal(4, searchUrls.Count);
    }

    [Fact]
    public async Task TryOpenLibrary_CoverImageReturns404_FallsToGoogle()
    {
        var (fetcher, handler) = CreateFetcherWithMockHttp();

        handler.AddJsonResponse("openlibrary.org/search.json", new
        {
            docs = new[]
            {
                new { title = "Book", cover_i = 333 }
            }
        });

        handler.AddNotFound("covers.openlibrary.org");

        handler.AddJsonResponse("googleapis.com/books", new
        {
            items = new[]
            {
                new
                {
                    volumeInfo = new
                    {
                        imageLinks = new { thumbnail = "https://books.google.com/img?zoom=1" }
                    }
                }
            }
        });
        handler.AddImageResponse("books.google.com", CreateFakeJpeg());

        var result = await fetcher.FetchCoverAsync("Book", "Author", CancellationToken.None);
        Assert.NotNull(result);
        result!.Value.Stream.Dispose();
    }

    [Fact]
    public async Task TryGoogleBooks_NoVolumeInfo_ReturnsNull()
    {
        var (fetcher, handler) = CreateFetcherWithMockHttp();

        handler.AddJsonResponse("openlibrary.org/search.json", new { docs = Array.Empty<object>() });

        // items exist but no volumeInfo
        handler.AddJsonResponse("googleapis.com/books", new
        {
            items = new[]
            {
                new { kind = "books#volume" }
            }
        });

        var result = await fetcher.FetchCoverAsync("Test Book", null, CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task TryOpenLibrary_MultipleDocs_UsesFirstWithCover()
    {
        var (fetcher, handler) = CreateFetcherWithMockHttp();

        // Multiple docs, only second has cover_i
        var json = """{"docs":[{"title":"Book A"},{"title":"Book B","cover_i":555}]}""";
        handler.AddResponse("openlibrary.org/search.json", HttpStatusCode.OK,
            Encoding.UTF8.GetBytes(json));

        handler.AddImageResponse("covers.openlibrary.org", CreateFakeJpeg());

        var result = await fetcher.FetchCoverAsync("Book", null, CancellationToken.None);
        Assert.NotNull(result);

        var coverUrl = handler.RequestedUrls.FirstOrDefault(u => u.Contains("covers.openlibrary.org"));
        Assert.NotNull(coverUrl);
        Assert.Contains("555", coverUrl);

        result!.Value.Stream.Dispose();
    }
}
