using System.Runtime.InteropServices;

namespace SmartCovers;

/// <summary>
/// Locates and loads the bundled pdfium native library. This is the single source of
/// truth for native resolution, shared by the PDFtoImage P/Invoke resolver
/// (<see cref="Plugin"/>) and the PDF availability probe (<c>CoverImageProvider</c>).
/// </summary>
/// <remarks>
/// The availability probe MUST confirm the native loads here BEFORE any PDFtoImage API
/// is touched. PDFtoImage's <c>PdfLibrary</c> registers a finalizer the instant it is
/// allocated; if its constructor then fails because pdfium is absent or unloadable (a
/// Windows host without the bundled dll, Alpine/musl, an arm64/x64 mismatch, or a
/// corrupt native download — on any OS), the orphaned object's finalizer later
/// re-invokes <c>FPDF_DestroyLibrary</c> on the GC finalizer thread. That unhandled
/// native exception cannot be caught and terminates the entire host process. Loading
/// the native directly here — touching no PDFtoImage type — means no <c>PdfLibrary</c>
/// is ever constructed when pdfium cannot load, so the crash cannot occur.
/// </remarks>
internal static class PdfiumNativeLibrary
{
    private static readonly object _loadLock = new();
    private static IntPtr _handle;

    /// <summary>
    /// Loads the pdfium native library once and pins the handle for the process
    /// lifetime, so the availability probe and PDFtoImage's later P/Invoke resolve to
    /// the SAME resident library. A successful load therefore guarantees that rendering
    /// can never hit a failed native load (which would otherwise resurrect the finalizer
    /// crash via a half-constructed <c>PdfLibrary</c>). The handle is released implicitly
    /// at process exit; it is never unloaded.
    /// </summary>
    /// <param name="handle">The loaded native library handle, or <see cref="IntPtr.Zero"/> on failure.</param>
    /// <returns><see langword="true"/> if pdfium is loaded; otherwise <see langword="false"/>.</returns>
    internal static bool TryLoad(out IntPtr handle)
    {
        // Fast path: pdfium is already loaded and pinned. The volatile read pairs with
        // the volatile write below so the double-checked lock is correct on weak memory
        // models (arm64 — Raspberry Pi, Apple Silicon). IntPtr is pointer-sized and
        // naturally aligned, so the read/write is atomic on 32-bit ARM too.
        var cached = Volatile.Read(ref _handle);
        if (cached != IntPtr.Zero)
        {
            handle = cached;
            return true;
        }

        lock (_loadLock)
        {
            cached = _handle;
            if (cached != IntPtr.Zero)
            {
                handle = cached;
                return true;
            }

            var pluginDir = Path.GetDirectoryName(typeof(PdfiumNativeLibrary).Assembly.Location);
            if (pluginDir != null)
            {
                foreach (var path in EnumerateCandidates(pluginDir))
                {
                    // NativeLibrary.TryLoad returns false (never throws) when the file
                    // is missing, a dependency is missing, or the architecture
                    // mismatches — so iterating candidates and taking the first that
                    // loads is safe.
                    if (NativeLibrary.TryLoad(path, out var loaded))
                    {
                        Volatile.Write(ref _handle, loaded);
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
    /// Yields candidate filesystem paths for the pdfium native library, most-specific
    /// first, using the runtime's reported RID and a derived portable RID.
    /// </summary>
    internal static IEnumerable<string> EnumerateCandidates(string pluginDir) =>
        EnumerateCandidates(pluginDir, RuntimeInformation.RuntimeIdentifier, GetPortableRid(), GetNativeFileName());

    /// <summary>
    /// Yields candidate paths for the given inputs. Exposed for tests so the ordering and
    /// de-duplication can be verified without depending on the host's RID.
    /// </summary>
    /// <param name="pluginDir">The plugin install directory.</param>
    /// <param name="selfRid">The runtime's self-reported RID (e.g. <c>linux-x64</c> or a distro RID like <c>ubuntu.24.10-x64</c>).</param>
    /// <param name="portableRid">The derived portable RID (e.g. <c>linux-x64</c>), or <see langword="null"/>.</param>
    /// <param name="nativeFileName">The platform native file name (e.g. <c>libpdfium.so</c>).</param>
    internal static IEnumerable<string> EnumerateCandidates(string pluginDir, string selfRid, string? portableRid, string nativeFileName)
    {
        // 1. The runtime's self-reported RID — matches the bundled folder on the
        //    portable Microsoft runtimes (e.g. "linux-x64", "win-x64", "osx-arm64").
        yield return Path.Combine(pluginDir, "runtimes", selfRid, "native", nativeFileName);

        // 2. A portable RID derived from OS + process architecture. Distro-packaged or
        //    source-built .NET can report a distro-specific RID (e.g. "ubuntu.24.10-x64")
        //    for which no runtimes/<rid>/ folder is shipped; the portable RID is the
        //    folder the build actually bundles. Without this fallback, those installs
        //    (Proxmox/LXC, apt-packaged dotnet) would lose PDF rendering even though the
        //    correct native is present under a differently-named folder.
        if (portableRid != null && !string.Equals(portableRid, selfRid, StringComparison.OrdinalIgnoreCase))
        {
            yield return Path.Combine(pluginDir, "runtimes", portableRid, "native", nativeFileName);
        }

        // 3. Flat layout: the native sitting directly in the plugin root (manual installs).
        yield return Path.Combine(pluginDir, nativeFileName);

        // 4. Last resort: any bundled runtimes/**/native/<file>. The OS loader rejects
        //    architecture mismatches (TryLoad returns false), so probing each match is
        //    safe and rescues an unforeseen RID-folder mismatch (e.g. a musl host that
        //    reports a non-musl RID) instead of silently disabling PDF.
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
    /// Gets the platform-specific pdfium native file name.
    /// </summary>
    internal static string GetNativeFileName() =>
        OperatingSystem.IsWindows() ? "pdfium.dll"
        : OperatingSystem.IsMacOS() ? "libpdfium.dylib"
        : "libpdfium.so";

    /// <summary>
    /// Computes the portable .NET RID (e.g. <c>linux-x64</c>) from the current OS and
    /// process architecture, or <see langword="null"/> if the platform is unrecognized.
    /// </summary>
    internal static string? GetPortableRid()
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
}
