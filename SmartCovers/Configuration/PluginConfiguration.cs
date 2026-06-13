using MediaBrowser.Model.Plugins;

namespace SmartCovers.Configuration;

/// <summary>
/// Plugin configuration for cover extraction settings.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the DPI used for rendering the PDF first page.
    /// </summary>
    public int Dpi { get; set; } = 150;

    /// <summary>
    /// Gets or sets the JPEG quality (1-100) for the output cover image.
    /// </summary>
    public int JpegQuality { get; set; } = 85;

    /// <summary>
    /// Gets or sets the timeout in seconds for a single PDF render or ffmpeg invocation.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Gets or sets a value indicating whether online cover fetching is enabled.
    /// </summary>
    public bool EnableOnlineCoverFetch { get; set; } = true;
}
