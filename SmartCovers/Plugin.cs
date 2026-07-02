using System.Diagnostics.CodeAnalysis;
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
[ExcludeFromCodeCoverage]
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    // Guards one-time registration of the pdfium DllImport resolver. The
    // AssemblyLoadContext.Resolving handler is not serialized across threads for an
    // in-flight first bind, so two concurrent first-touches of PDFtoImage could both
    // reach SetDllImportResolver; a second call for the same assembly throws.
    private static int _pdfiumResolverRegistered;

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

                        // Register the native resolver so pdfium's P/Invoke finds the
                        // correct platform binary under runtimes/. Exactly once: the
                        // Resolving handler can fire concurrently on first touch, and a
                        // second SetDllImportResolver for the same assembly throws.
                        if (Interlocked.Exchange(ref _pdfiumResolverRegistered, 1) == 0)
                        {
                            NativeLibrary.SetDllImportResolver(asm, ResolvePdfiumNative);
                        }

                        return asm;
                    }
                }
            }

            // SharpCompress (CBZ/CBR extraction): Jellyfin ships its own copy
            // (MediaBrowser.Providers → Default ALC), which plugin code binds to
            // through the Default-ALC fallback — the csproj pins the same version,
            // so the API surface matches. This branch only fires if a future
            // Jellyfin stops shipping SharpCompress; then the copy bundled in the
            // plugin folder takes over. The "assemblies" whitelist in meta.json
            // keeps the plugin scanner from GetTypes()-scanning the bundled DLL at
            // startup (that scan, not runtime resolution, is what breaks plugins).
            if (string.Equals(name.Name, "SharpCompress", StringComparison.OrdinalIgnoreCase))
            {
                var pluginDir = Path.GetDirectoryName(typeof(Plugin).Assembly.Location);
                if (pluginDir != null)
                {
                    var dllPath = Path.Combine(pluginDir, "SharpCompress.dll");
                    if (File.Exists(dllPath))
                    {
                        return AssemblyLoadContext.Default.LoadFromAssemblyPath(dllPath);
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

        // Delegate to the shared loader (see PdfiumNativeLibrary for the full
        // finalizer-crash rationale). The handle is pinned, so this resolves the same
        // resident library the availability probe already confirmed.
        return PdfiumNativeLibrary.TryLoad(out var handle) ? handle : IntPtr.Zero;
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
