using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using SmartCovers.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace SmartCovers;

/// <summary>
/// Cover provider for Jellyfin libraries. Extracts covers from PDFs, EPUBs,
/// audio files, and fetches from online sources as a last resort.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    // Jellyfin's plugin loader scans all .dll files in the plugin directory and
    // rejects any that reference a different version of a shared library (e.g.
    // SkiaSharp). PDFtoImage.dll references SkiaSharp 3.119.2 while Jellyfin
    // ships 3.116.1, causing BadImageFormatException/version-mismatch errors.
    //
    // Workaround: the CI renames PDFtoImage.dll → PDFtoImage.lib so Jellyfin's
    // scanner ignores it. This static constructor registers a resolver that
    // loads the renamed file when PDFtoImage types are first accessed at runtime.
    static Plugin()
    {
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            if (string.Equals(name.Name, "PDFtoImage", StringComparison.OrdinalIgnoreCase))
            {
                var pluginDir = Path.GetDirectoryName(typeof(Plugin).Assembly.Location);
                if (pluginDir != null)
                {
                    var libPath = Path.Combine(pluginDir, "PDFtoImage.lib");
                    if (File.Exists(libPath))
                    {
                        var asm = AssemblyLoadContext.Default.LoadFromAssemblyPath(libPath);

                        // Register native library resolver so pdfium's P/Invoke
                        // finds the correct platform binary under runtimes/.
                        NativeLibrary.SetDllImportResolver(asm, ResolvePdfiumNative);

                        return asm;
                    }
                }
            }

            // PDFtoImage references SkiaSharp 3.119.x but Jellyfin ships 3.116.x.
            // Redirect to the version already loaded by Jellyfin — the API surface
            // for the operations PDFtoImage uses is identical across 3.x minors.
            if (string.Equals(name.Name, "SkiaSharp", StringComparison.OrdinalIgnoreCase))
            {
                return AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => string.Equals(
                        a.GetName().Name, "SkiaSharp", StringComparison.OrdinalIgnoreCase));
            }

            return null;
        };
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <inheritdoc />
    public override string Name => "SmartCovers";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("82eef869-3f18-4678-968d-06efc10b60cf");

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    private static IntPtr ResolvePdfiumNative(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, "pdfium", StringComparison.OrdinalIgnoreCase))
        {
            return IntPtr.Zero;
        }

        var pluginDir = Path.GetDirectoryName(typeof(Plugin).Assembly.Location);
        if (pluginDir == null)
        {
            return IntPtr.Zero;
        }

        // Build candidate paths: platform-specific runtimes dir, then plugin root.
        var rid = RuntimeInformation.RuntimeIdentifier;
        string nativeFileName = OperatingSystem.IsWindows() ? "pdfium.dll"
            : OperatingSystem.IsMacOS() ? "libpdfium.dylib"
            : "libpdfium.so";

        string[] candidates =
        [
            Path.Combine(pluginDir, "runtimes", rid, "native", nativeFileName),
            Path.Combine(pluginDir, nativeFileName),
        ];

        foreach (var path in candidates)
        {
            if (NativeLibrary.TryLoad(path, out var handle))
            {
                return handle;
            }
        }

        return IntPtr.Zero;
    }

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
