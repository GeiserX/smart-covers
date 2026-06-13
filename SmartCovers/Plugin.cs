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
    // The pdfium native library handle, loaded once and pinned for the process
    // lifetime so the availability probe and PDFtoImage's P/Invoke share the same
    // resident library. IntPtr.Zero means "not yet loaded / not loadable".
    private static readonly object _pdfiumLoadLock = new();
    private static IntPtr _pdfiumHandle;

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

        return TryLoadPdfiumNative(out var handle) ? handle : IntPtr.Zero;
    }

    /// <summary>
    /// Attempts to load the bundled pdfium native library from the plugin's
    /// <c>runtimes/&lt;rid&gt;/native/</c> layout.
    /// </summary>
    /// <remarks>
    /// This is the single source of truth for locating pdfium. It is used both by
    /// the <see cref="NativeLibrary.SetDllImportResolver"/> callback (so PDFtoImage's
    /// P/Invoke resolves correctly) AND by <c>CoverImageProvider</c>'s availability
    /// probe — the probe MUST confirm the native is loadable here BEFORE touching any
    /// PDFtoImage API, because PDFtoImage's <c>PdfLibrary</c> arms a finalizer on
    /// allocation; if its constructor then fails (pdfium absent/unloadable), the
    /// orphaned finalizer re-invokes <c>FPDF_DestroyLibrary</c> on the GC finalizer
    /// thread, and that unhandled native exception terminates the whole host process.
    /// </remarks>
    /// <param name="handle">The loaded native library handle, or <see cref="IntPtr.Zero"/> on failure.</param>
    /// <returns><see langword="true"/> if pdfium was loaded; otherwise <see langword="false"/>.</returns>
    internal static bool TryLoadPdfiumNative(out IntPtr handle)
    {
        // Fast path: pdfium is already loaded and pinned for the process lifetime.
        // Volatile read pairs with the Volatile.Write below so the double-checked
        // lock is correct on weak memory models (arm64 — Raspberry Pi, Apple Silicon).
        var cached = Volatile.Read(ref _pdfiumHandle);
        if (cached != IntPtr.Zero)
        {
            handle = cached;
            return true;
        }

        lock (_pdfiumLoadLock)
        {
            cached = _pdfiumHandle;
            if (cached != IntPtr.Zero)
            {
                handle = cached;
                return true;
            }

            var pluginDir = Path.GetDirectoryName(typeof(Plugin).Assembly.Location);
            if (pluginDir != null)
            {
                foreach (var path in EnumeratePdfiumCandidates(pluginDir))
                {
                    // NativeLibrary.TryLoad returns false (never throws) when the file
                    // is missing, a dependency is missing, or the architecture
                    // mismatches — so iterating candidates and taking the first that
                    // loads is safe.
                    if (NativeLibrary.TryLoad(path, out var loaded))
                    {
                        // Pin the handle: once pdfium is resident, the availability
                        // probe and PDFtoImage's later P/Invoke resolve to the SAME
                        // loaded library. A successful probe therefore guarantees that
                        // rendering can never hit a failed native load — which is what
                        // would otherwise resurrect the issue #11 finalizer crash via a
                        // half-constructed PdfLibrary. The handle is released implicitly
                        // at process exit; we never unload it.
                        Volatile.Write(ref _pdfiumHandle, loaded);
                        handle = loaded;
                        return true;
                    }
                }
            }
        }

        handle = IntPtr.Zero;
        return false;
    }

    /// <summary>
    /// Yields candidate filesystem paths for the pdfium native library, most-specific first.
    /// </summary>
    private static IEnumerable<string> EnumeratePdfiumCandidates(string pluginDir)
    {
        string nativeFileName = OperatingSystem.IsWindows() ? "pdfium.dll"
            : OperatingSystem.IsMacOS() ? "libpdfium.dylib"
            : "libpdfium.so";

        // 1. The runtime's self-reported RID — matches the bundled folder on the
        //    portable Microsoft runtimes (e.g. "linux-x64", "win-x64", "osx-arm64").
        var rid = RuntimeInformation.RuntimeIdentifier;
        yield return Path.Combine(pluginDir, "runtimes", rid, "native", nativeFileName);

        // 2. A portable RID derived from OS + process architecture. Distro-packaged
        //    or source-built .NET can report a distro-specific RID (e.g.
        //    "ubuntu.24.10-x64") for which no runtimes/<rid>/ folder is shipped;
        //    the portable RID is the folder the build actually bundles. Without this
        //    fallback, those installs would lose PDF rendering even though the
        //    correct native is present under a differently-named folder.
        var portableRid = GetPortableRid();
        if (portableRid != null && !string.Equals(portableRid, rid, StringComparison.OrdinalIgnoreCase))
        {
            yield return Path.Combine(pluginDir, "runtimes", portableRid, "native", nativeFileName);
        }

        // 3. Flat layout: the native sitting directly in the plugin root.
        yield return Path.Combine(pluginDir, nativeFileName);

        // 4. Last resort: any bundled runtimes/**/native/<file>. The OS loader
        //    rejects architecture mismatches (TryLoad returns false), so probing
        //    each match is safe and rescues an unforeseen RID-folder mismatch
        //    instead of silently disabling PDF.
        var runtimesDir = Path.Combine(pluginDir, "runtimes");
        if (Directory.Exists(runtimesDir))
        {
            foreach (var match in Directory.EnumerateFiles(runtimesDir, nativeFileName, SearchOption.AllDirectories))
            {
                yield return match;
            }
        }
    }

    /// <summary>
    /// Computes the portable .NET RID (e.g. <c>linux-x64</c>) from the current OS and
    /// process architecture, or <see langword="null"/> if the platform is unrecognized.
    /// </summary>
    private static string? GetPortableRid()
    {
        string? os = OperatingSystem.IsWindows() ? "win"
            : OperatingSystem.IsMacOS() ? "osx"
            : OperatingSystem.IsLinux() ? "linux"
            : null;

        if (os == null)
        {
            return null;
        }

        string? arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            Architecture.Arm => "arm",
            _ => null
        };

        return arch == null ? null : $"{os}-{arch}";
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
