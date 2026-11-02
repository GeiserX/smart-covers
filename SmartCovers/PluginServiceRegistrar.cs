using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace SmartCovers;

/// <summary>
/// Registers plugin services for dependency injection.
/// </summary>
public class PluginServiceRegistrar : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<CoverOutcomeLog>();
        serviceCollection.AddSingleton<OnlineCoverFetcher>();
        serviceCollection.AddSingleton<CoverImageProvider>();
    }
}
