using System.Globalization;
using Jellyfin.Plugin.PdfCover.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.PdfCover;

/// <summary>
/// Fallback cover provider for Jellyfin book and audiobook libraries. Extracts
/// PDF first pages via pdftoppm, searches EPUB archives for cover images, and
/// extracts embedded artwork from audio files via ffmpeg raw stream copy.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <inheritdoc />
    public override string Name => "Book Cover";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("82eef869-3f18-4678-968d-06efc10b60cf");

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}.Configuration.configPage.html",
                    GetType().Namespace),
                EnableInMainMenu = true
            }
        ];
    }
}
