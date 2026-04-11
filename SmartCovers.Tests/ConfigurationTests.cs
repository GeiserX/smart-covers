using SmartCovers.Configuration;
using Xunit;

namespace SmartCovers.Tests;

public class ConfigurationTests
{
    [Fact]
    public void PluginConfiguration_DefaultValues()
    {
        var config = new PluginConfiguration();

        Assert.Equal(150, config.Dpi);
        Assert.Equal(85, config.JpegQuality);
        Assert.Equal(30, config.TimeoutSeconds);
        Assert.True(config.EnableOnlineCoverFetch);
    }

    [Fact]
    public void PluginConfiguration_CanSetValues()
    {
        var config = new PluginConfiguration
        {
            Dpi = 300,
            JpegQuality = 95,
            TimeoutSeconds = 60,
            EnableOnlineCoverFetch = false,
        };

        Assert.Equal(300, config.Dpi);
        Assert.Equal(95, config.JpegQuality);
        Assert.Equal(60, config.TimeoutSeconds);
        Assert.False(config.EnableOnlineCoverFetch);
    }
}
