using Moq;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Serialization;
using SmartCovers.Configuration;
using Xunit;

namespace SmartCovers.Tests;

public class PluginTests
{
    private static Plugin CreatePlugin()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"smartcovers-plugin-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        var appPaths = new Mock<IApplicationPaths>();
        appPaths.SetupGet(p => p.PluginConfigurationsPath).Returns(tmpDir);
        appPaths.SetupGet(p => p.PluginsPath).Returns(tmpDir);
        appPaths.SetupGet(p => p.DataPath).Returns(tmpDir);

        // Ensure Configuration returns a valid object so other tests that depend
        // on Plugin.Instance.Configuration are not broken by parallel execution.
        var xmlMock = new Mock<IXmlSerializer>();
        xmlMock.Setup(x => x.DeserializeFromFile(It.IsAny<Type>(), It.IsAny<string>()))
            .Returns(new Configuration.PluginConfiguration { EnableOnlineCoverFetch = true });

        return new Plugin(appPaths.Object, xmlMock.Object);
    }

    [Fact]
    public void Plugin_Constructor_SetsInstance()
    {
        var plugin = CreatePlugin();
        Assert.NotNull(Plugin.Instance);
        Assert.Same(plugin, Plugin.Instance);
    }

    [Fact]
    public void Plugin_Name_ReturnsSmartCovers()
    {
        var plugin = CreatePlugin();
        Assert.Equal("SmartCovers", plugin.Name);
    }

    [Fact]
    public void Plugin_Id_ReturnsExpectedGuid()
    {
        var plugin = CreatePlugin();
        Assert.Equal(Guid.Parse("82eef869-3f18-4678-968d-06efc10b60cf"), plugin.Id);
    }

    [Fact]
    public void Plugin_GetPages_ReturnsConfigPage()
    {
        var plugin = CreatePlugin();

        var pages = plugin.GetPages().ToList();
        Assert.Single(pages);
        Assert.Equal("SmartCovers", pages[0].Name);
        Assert.Contains("configPage.html", pages[0].EmbeddedResourcePath);
        Assert.True(pages[0].EnableInMainMenu);
    }

    [Fact]
    public void Plugin_Instance_IsNotNull_AfterConstruction()
    {
        var plugin = CreatePlugin();
        Assert.NotNull(Plugin.Instance);
        // Configuration defaults are tested in ConfigurationTests
    }
}
