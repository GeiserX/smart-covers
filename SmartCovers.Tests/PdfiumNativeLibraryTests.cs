using System.Text.RegularExpressions;
using Xunit;

namespace SmartCovers.Tests;

/// <summary>
/// Unit tests for <see cref="PdfiumNativeLibrary"/>'s pure resolution logic — the RID
/// derivation, native file-name selection, and candidate-path ordering/de-duplication.
///
/// These cover a HIGH-severity gap flagged in review: the RID/candidate logic that decides
/// where pdfium is loaded from (and therefore whether PDF rendering works on distro-packaged
/// or musl .NET installs) previously had ZERO coverage. The candidate ordering is asserted
/// through the pure <c>EnumerateCandidates(pluginDir, selfRid, portableRid, nativeFileName)</c>
/// overload so the result never depends on the host's real RID — only the final
/// <c>AllDirectories</c> walk touches disk, and a non-existent <c>pluginDir</c> makes it a no-op.
/// </summary>
public class PdfiumNativeLibraryTests
{
    // ---------------------------------------------------------------------
    // GetPortableRid — derives "os-arch" from the current OS + process arch.
    // ---------------------------------------------------------------------

    [Fact]
    public void GetPortableRid_ReturnsNonNull_OnCurrentPlatform()
    {
        // The test host is always one of the recognised OS/arch combinations, so the
        // derived RID must be a non-null "os-arch" string in the supported set. A null
        // here would mean an unrecognised platform/architecture silently disabling the
        // portable-RID fallback.
        var rid = PdfiumNativeLibrary.GetPortableRid();

        Assert.NotNull(rid);
        Assert.Matches("^(win|osx|linux)-(x64|arm64|x86|arm)$", rid);
    }

    // ---------------------------------------------------------------------
    // GetNativeFileName — platform-specific pdfium file name.
    // ---------------------------------------------------------------------

    [Fact]
    public void GetNativeFileName_MatchesCurrentOS()
    {
        // Derive the expectation the same way production does, from the runtime OS check,
        // so this test stays correct on every CI runner (Windows/macOS/Linux).
        var expected =
            OperatingSystem.IsWindows() ? "pdfium.dll"
            : OperatingSystem.IsMacOS() ? "libpdfium.dylib"
            : "libpdfium.so";

        Assert.Equal(expected, PdfiumNativeLibrary.GetNativeFileName());
    }

    // ---------------------------------------------------------------------
    // EnumerateCandidates — ordering and de-duplication of probe paths.
    // ---------------------------------------------------------------------

    [Fact]
    public void EnumerateCandidates_OrdersSelfRidFirst()
    {
        // selfRid is a distro-specific RID with no bundled folder; portableRid is the
        // folder the build actually ships. The most-specific (self) path must come first,
        // the portable path second, the flat layout third. pluginDir does not exist on
        // disk, so the final AllDirectories walk yields nothing → exactly 3 candidates.
        const string pluginDir = "/tmp/plugin";
        const string selfRid = "ubuntu.24.10-x64";
        const string portableRid = "linux-x64";
        const string nativeFileName = "libpdfium.so";

        var candidates = PdfiumNativeLibrary
            .EnumerateCandidates(pluginDir, selfRid, portableRid, nativeFileName)
            .ToList();

        Assert.Equal(3, candidates.Count);
        Assert.Equal(Path.Combine(pluginDir, "runtimes", selfRid, "native", nativeFileName), candidates[0]);
        Assert.Equal(Path.Combine(pluginDir, "runtimes", portableRid, "native", nativeFileName), candidates[1]);
        Assert.Equal(Path.Combine(pluginDir, nativeFileName), candidates[2]);
    }

    [Fact]
    public void EnumerateCandidates_SkipsPortableRid_WhenSameAsSelfRid()
    {
        // When the portable RID equals the self RID there must be no duplicate runtimes
        // path: only the self path and the flat path are yielded.
        const string pluginDir = "/tmp/plugin";
        const string selfRid = "linux-x64";
        const string portableRid = "linux-x64";
        const string nativeFileName = "libpdfium.so";

        var candidates = PdfiumNativeLibrary
            .EnumerateCandidates(pluginDir, selfRid, portableRid, nativeFileName)
            .ToList();

        Assert.Equal(2, candidates.Count);
        Assert.Equal(Path.Combine(pluginDir, "runtimes", selfRid, "native", nativeFileName), candidates[0]);
        Assert.Equal(Path.Combine(pluginDir, nativeFileName), candidates[1]);
    }

    [Fact]
    public void EnumerateCandidates_SkipsPortableRid_WhenNull()
    {
        // A null portable RID (unrecognised platform) must not produce a runtimes/<null>
        // path — only the self path and the flat path are yielded.
        const string pluginDir = "/tmp/plugin";
        const string selfRid = "linux-x64";
        const string nativeFileName = "libpdfium.so";

        var candidates = PdfiumNativeLibrary
            .EnumerateCandidates(pluginDir, selfRid, null, nativeFileName)
            .ToList();

        Assert.Equal(2, candidates.Count);
        Assert.Equal(Path.Combine(pluginDir, "runtimes", selfRid, "native", nativeFileName), candidates[0]);
        Assert.Equal(Path.Combine(pluginDir, nativeFileName), candidates[1]);
    }

    [Fact]
    public void EnumerateCandidates_IncludesPortableRid_WhenDifferent()
    {
        // Distinct self/portable RIDs (case-insensitively different) must surface the
        // portable path at index 1, between the self path and the flat path.
        const string pluginDir = "/tmp/plugin";
        const string selfRid = "win-x64";
        const string portableRid = "win-arm64";
        const string nativeFileName = "pdfium.dll";

        var candidates = PdfiumNativeLibrary
            .EnumerateCandidates(pluginDir, selfRid, portableRid, nativeFileName)
            .ToList();

        Assert.Equal(3, candidates.Count);
        Assert.Equal(Path.Combine(pluginDir, "runtimes", portableRid, "native", nativeFileName), candidates[1]);
    }

    [Fact]
    public void EnumerateCandidates_AllDirectoriesWalk_SurfacesUnmatchedRidFolder()
    {
        // Last-resort safety net: when neither the self nor portable RID matches the
        // bundled folder name (e.g. a musl host reporting a non-musl RID), the recursive
        // runtimes/**/native/<file> walk must still surface the present native. We lay
        // down a real temp tree with the native under a folder name that matches NEITHER
        // the self nor portable RID, then assert it appears among the candidates.
        var pluginDir = Path.Combine(Path.GetTempPath(), $"smartcovers-rid-{Guid.NewGuid():N}");
        const string selfRid = "linux-x64";
        const string portableRid = "linux-x64";
        const string nativeFileName = "libpdfium.so";

        var muslNativeDir = Path.Combine(pluginDir, "runtimes", "linux-musl-x64", "native");
        Directory.CreateDirectory(muslNativeDir);
        var muslNativePath = Path.Combine(muslNativeDir, nativeFileName);
        File.WriteAllText(muslNativePath, "dummy-native");

        try
        {
            var candidates = PdfiumNativeLibrary
                .EnumerateCandidates(pluginDir, selfRid, portableRid, nativeFileName)
                .ToList();

            // self + flat (2) plus the one match found by the AllDirectories walk.
            Assert.Equal(3, candidates.Count);
            Assert.Equal(Path.Combine(pluginDir, "runtimes", selfRid, "native", nativeFileName), candidates[0]);
            Assert.Equal(Path.Combine(pluginDir, nativeFileName), candidates[1]);
            Assert.Equal(muslNativePath, candidates[2]);
        }
        finally
        {
            Directory.Delete(pluginDir, recursive: true);
        }
    }
}
