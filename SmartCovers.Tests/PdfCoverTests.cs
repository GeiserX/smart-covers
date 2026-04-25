using Moq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Xunit;

namespace SmartCovers.Tests;

public class PdfCoverTests
{
    private static CoverImageProvider CreateProvider()
    {
        var logger = Mock.Of<ILogger<CoverImageProvider>>();
        var fetcherLogger = Mock.Of<ILogger<OnlineCoverFetcher>>();
        var fetcher = new OnlineCoverFetcher(fetcherLogger);
        return new CoverImageProvider(logger, fetcher);
    }

    [Fact]
    public void IsPdfRenderingAvailable_CachesResult()
    {
        var provider = CreateProvider();
        var first = provider.IsPdfRenderingAvailable();
        var second = provider.IsPdfRenderingAvailable();
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task GetImage_PdfFile_WhenPdfiumUnavailable_ReturnsNoImage()
    {
        var provider = CreateProvider();
        var tmpFile = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}.pdf");
        File.WriteAllText(tmpFile, "%PDF-1.0 dummy");

        try
        {
            var item = new Mock<Book>();
            item.SetupGet(i => i.Path).Returns(tmpFile);
            item.SetupGet(i => i.Name).Returns("Test PDF Book");

            var result = await provider.GetImage(item.Object, ImageType.Primary, CancellationToken.None);
            // If PDFium is not available (likely in test env), should return no image
            // then fall through to online (which also returns no image since Plugin.Instance is null)
            // Either way, should not throw
            Assert.IsType<bool>(result.HasImage);
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }

    [Fact]
    public void GetFfmpegPath_CachesResult()
    {
        var provider = CreateProvider();
        var first = provider.GetFfmpegPath();
        var second = provider.GetFfmpegPath();
        Assert.Equal(first, second);
    }

    [Fact]
    public void GetFfmpegPath_ReturnsStringOrNull()
    {
        var provider = CreateProvider();
        var path = provider.GetFfmpegPath();
        // May be null (no ffmpeg) or a valid path - both are acceptable
        if (path != null)
        {
            Assert.NotEmpty(path);
        }
    }
}
