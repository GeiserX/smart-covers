using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Moq;
using SkiaSharp;
using Xunit;

namespace SmartCovers.Tests;

/// <summary>
/// What Google Books handed back on a real library on 2026-11-03 once it had an API
/// key: its "image not available" picture, strips cut from the top of a title page,
/// and another author's book. Every fixture under <c>Fixtures/google/</c> is the
/// exact file Google served.
/// </summary>
public class GoogleBooksCoverTests
{
    private static byte[] Fixture(string name)
        => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "google", name));

    private static OnlineCoverFetcher Fetcher(HttpMessageHandler handler)
        => new(Mock.Of<ILogger<OnlineCoverFetcher>>(), new HttpClient(handler)) { GoogleBooksApiKeySource = () => "test-key" };

    private static HttpResponseMessage Image(byte[] bytes, string type)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(type);
        return response;
    }

    private static object Volume(string author, string thumbnail)
        => new
        {
            items = new[]
            {
                new { volumeInfo = new { authors = new[] { author }, imageLinks = new { thumbnail } } }
            }
        };

    [Fact]
    public void Placeholder_IsNotACover()
        => Assert.Contains("image not available", CoverImageCheck.Problem(Fixture("placeholder.png"), 0), StringComparison.Ordinal);

    [Fact]
    public void Placeholder_AtThumbnailSizeAsJpeg_IsStillRecognised()
    {
        // The same picture by content, not by its bytes: 128 wide, re-encoded.
        using var original = SKBitmap.Decode(Fixture("placeholder.png"));
        using var small = original.Resize(new SKImageInfo(128, 167), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        using var jpeg = small.Encode(SKEncodedImageFormat.Jpeg, 70);

        Assert.NotNull(CoverImageCheck.Problem(jpeg.ToArray(), 0));
    }

    [Theory]
    [InlineData("strip-entre-el-deshonor.png")]
    [InlineData("strip-ordenanzas.png")]
    [InlineData("strip-questione-agraria.png")]
    public void TitlePageStrip_IsTooWideForACover(string name)
        => Assert.Contains("too wide", CoverImageCheck.Problem(Fixture(name), 0), StringComparison.Ordinal);

    [Theory]
    [InlineData("title-page-domination-espagnole.png")]
    [InlineData("rota-se-camina-igual.png")]
    [InlineData("thumbnail-dizionario-calabria.jpg")]
    [InlineData("thumbnail-ordenanzas.jpg")]
    public void RealCoversAndTitlePages_AreAccepted(string name)
        => Assert.Null(CoverImageCheck.Problem(Fixture(name), 0));

    [Fact]
    public void BlankPage_IsNotTakenForThePlaceholder()
    {
        using var bitmap = new SKBitmap(575, 750);
        bitmap.Erase(SKColors.White);
        using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);

        Assert.Null(CoverImageCheck.Problem(png.ToArray(), 0));
    }

    [Fact]
    public void SquareAudiobookCover_IsAccepted()
    {
        using var bitmap = new SKBitmap(500, 500);
        bitmap.Erase(SKColors.DarkRed);
        using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);

        Assert.Null(CoverImageCheck.Problem(png.ToArray(), 0));
    }

    [Theory]
    [InlineData("placeholder.png")]
    [InlineData("strip-ordenanzas.png")]
    public async Task ZoomZeroRejected_TheThumbnailTheApiGaveIsUsed(string zoomZero)
    {
        var handler = new OrderedMockHandler();
        handler.WhenUrlContains("openlibrary.org/search", new { docs = Array.Empty<object>() });
        handler.WhenUrlContains(
            "googleapis.com/books",
            Volume("José Manuel Almendros Toledo", "http://books.google.com/books/content?id=hXEwAAAAYAAJ&printsec=frontcover&img=1&zoom=1&source=gbs_api"));
        handler.When(url => url.Contains("zoom=0", StringComparison.Ordinal), () => Image(Fixture(zoomZero), "image/png"));
        handler.When(url => url.Contains("zoom=1", StringComparison.Ordinal), () => Image(Fixture("thumbnail-ordenanzas.jpg"), "image/jpeg"));

        var lookup = await Fetcher(handler).LookupAsync(
            new BookQuery("Ordenanzas municipales de la Ribera del Júcar", "José Manuel Almendros Toledo", []), CancellationToken.None);

        Assert.NotNull(lookup.Cover);
        Assert.Equal(Fixture("thumbnail-ordenanzas.jpg").Length, lookup.Cover!.Value.Stream.Length);
        Assert.Equal("Google Books", lookup.Source);
    }

    [Fact]
    public async Task PlaceholderAtEveryZoom_IsAMiss()
    {
        var handler = new OrderedMockHandler();
        handler.WhenUrlContains("openlibrary.org/search", new { docs = Array.Empty<object>() });
        handler.WhenUrlContains(
            "googleapis.com/books",
            Volume("Gustavo Valente", "http://books.google.com/books/content?id=k4RWAAAAYAAJ&printsec=frontcover&img=1&zoom=1&source=gbs_api"));
        handler.When(url => url.Contains("books.google.com", StringComparison.Ordinal), () => Image(Fixture("placeholder.png"), "image/png"));

        var lookup = await Fetcher(handler).LookupAsync(
            new BookQuery("Dizionario dei luoghi della Calabria", "Gustavo Valente", []), CancellationToken.None);

        Assert.Null(lookup.Cover);
        Assert.False(lookup.NetworkError);
        Assert.Contains(handler.RequestedUrls, u => u.Contains("zoom=1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnotherAuthorsBook_IsNeverTaken()
    {
        // Google's real answer to "Camine Con Gusto": the book itself without a
        // cover, then Lorena Pronsky's "Rota se camina igual" with one.
        var (title, author) = OnlineCoverFetcher.ParseName("Camine Con Gusto (Mp3-Voz Humana)- The Arthritis Foundation");
        Assert.Equal("The Arthritis Foundation", author);

        var handler = new OrderedMockHandler();
        handler.WhenUrlContains("openlibrary.org/search", new { docs = Array.Empty<object>() });
        handler.When(
            url => url.Contains("googleapis.com/books", StringComparison.Ordinal),
            () => Image(Fixture("camine-con-gusto-volumes.json"), "application/json"));
        handler.When(url => url.Contains("books.google.com", StringComparison.Ordinal), () => Image(Fixture("rota-se-camina-igual.png"), "image/png"));

        var lookup = await Fetcher(handler).LookupAsync(new BookQuery(title, author, []), CancellationToken.None);

        Assert.Null(lookup.Cover);
        Assert.Contains(handler.LookupSequence, l => l == $"GB q='{title}'");
        Assert.DoesNotContain(handler.RequestedUrls, u => u.Contains("books.google.com", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NoAuthorKnown_AShortTitleIsNotAskedOfGoogle()
    {
        var handler = new OrderedMockHandler();
        handler.WhenUrlContains("openlibrary.org/search", new { docs = Array.Empty<object>() });
        handler.When(
            url => url.Contains("googleapis.com/books", StringComparison.Ordinal),
            () => Image(Fixture("camine-con-gusto-volumes.json"), "application/json"));

        var lookup = await Fetcher(handler).LookupAsync(new BookQuery("Camine Con Gusto", null, []), CancellationToken.None);

        Assert.Null(lookup.Cover);
        Assert.DoesNotContain(handler.RequestedUrls, u => u.Contains("googleapis.com", StringComparison.Ordinal));
    }
}
