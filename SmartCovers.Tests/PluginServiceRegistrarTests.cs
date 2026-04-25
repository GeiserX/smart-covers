using Moq;
using MediaBrowser.Controller;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace SmartCovers.Tests;

public class PluginServiceRegistrarTests
{
    [Fact]
    public void RegisterServices_AddsExpectedServices()
    {
        var registrar = new PluginServiceRegistrar();
        var services = new ServiceCollection();
        var appHost = Mock.Of<IServerApplicationHost>();

        registrar.RegisterServices(services, appHost);

        // Verify both singletons were registered
        Assert.Contains(services, sd =>
            sd.ServiceType == typeof(OnlineCoverFetcher)
            && sd.Lifetime == ServiceLifetime.Singleton);

        Assert.Contains(services, sd =>
            sd.ServiceType == typeof(CoverImageProvider)
            && sd.Lifetime == ServiceLifetime.Singleton);
    }

    [Fact]
    public void RegisterServices_RegistersExactlyTwoServices()
    {
        var registrar = new PluginServiceRegistrar();
        var services = new ServiceCollection();
        var appHost = Mock.Of<IServerApplicationHost>();

        registrar.RegisterServices(services, appHost);

        Assert.Equal(2, services.Count);
    }
}
