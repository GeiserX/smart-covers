using Moq;
using Microsoft.Extensions.Logging;
using Xunit;

namespace SmartCovers.Tests;

public class CoverStatusControllerTests
{
    private static CoverImageProvider CreateProvider()
    {
        var logger = Mock.Of<ILogger<CoverImageProvider>>();
        var fetcherLogger = Mock.Of<ILogger<OnlineCoverFetcher>>();
        var fetcher = new OnlineCoverFetcher(fetcherLogger);
        return new CoverImageProvider(logger, fetcher);
    }

    [Fact]
    public void GetStatus_ReturnsStatus()
    {
        var provider = CreateProvider();
        var controller = new CoverStatusController(provider);

        var result = controller.GetStatus();
        Assert.NotNull(result.Value);
    }

    [Fact]
    public void GetStatus_HasExpectedProperties()
    {
        var provider = CreateProvider();
        var controller = new CoverStatusController(provider);

        var status = controller.GetStatus().Value!;
        // PdfRenderingAvailable depends on native library (likely false in test)
        // FfmpegAvailable depends on system (may be true or false)
        // OnlineCoverFetchEnabled defaults to true when Plugin.Instance is null
        Assert.True(status.OnlineCoverFetchEnabled);
    }

    [Fact]
    public void CoverStatus_PropertiesCanBeSet()
    {
        var status = new CoverStatus
        {
            PdfRenderingAvailable = true,
            FfmpegAvailable = true,
            OnlineCoverFetchEnabled = false
        };

        Assert.True(status.PdfRenderingAvailable);
        Assert.True(status.FfmpegAvailable);
        Assert.False(status.OnlineCoverFetchEnabled);
    }

    [Fact]
    public void CoverStatus_DefaultValues()
    {
        var status = new CoverStatus();
        Assert.False(status.PdfRenderingAvailable);
        Assert.False(status.FfmpegAvailable);
        Assert.False(status.OnlineCoverFetchEnabled);
    }
}
