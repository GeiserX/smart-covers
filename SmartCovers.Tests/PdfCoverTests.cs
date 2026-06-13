using Moq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Xunit;

namespace SmartCovers.Tests;

/// <summary>
/// Regression tests for issue #11 — the pdfium-finalizer host crash.
///
/// Background: the OLD <c>IsPdfRenderingAvailable()</c> probed for pdfium by calling
/// PDFtoImage's <c>Conversion.SaveJpeg(...)</c>. On any platform where the native lib
/// cannot load, PDFtoImage allocates a finalizable <c>PdfLibrary</c> whose constructor
/// throws <c>DllNotFoundException</c>; the orphaned object's finalizer later re-throws on
/// the GC finalizer thread, terminating the entire Jellyfin host process.
///
/// The fix replaces that probe with an injectable <see cref="System.Func{Boolean}"/> seam
/// (the internal ctor). These tests drive that seam directly so the no-native path can be
/// proven WITHOUT ever constructing a <c>PdfLibrary</c>. We deliberately do NOT force a GC
/// + WaitForPendingFinalizers here: under the old bug that would crash the test host, and
/// under the fix there is simply no finalizable object to observe — the seam is the whole
/// point and is what we assert on.
/// </summary>
public class PdfCoverTests
{
    /// <summary>Builds a provider using the REAL pdfium probe (public ctor).</summary>
    private static CoverImageProvider CreateProvider()
    {
        var logger = Mock.Of<ILogger<CoverImageProvider>>();
        var fetcherLogger = Mock.Of<ILogger<OnlineCoverFetcher>>();
        var fetcher = new OnlineCoverFetcher(fetcherLogger);
        return new CoverImageProvider(logger, fetcher);
    }

    /// <summary>
    /// Builds a provider with an injected pdfium-availability probe (internal ctor).
    /// This is the deterministic seam — no real native library is touched, and crucially
    /// no PDFtoImage type is ever constructed regardless of the probe's answer.
    /// </summary>
    private static CoverImageProvider CreateProvider(Func<bool> pdfiumNativeProbe)
    {
        var logger = Mock.Of<ILogger<CoverImageProvider>>();
        var fetcherLogger = Mock.Of<ILogger<OnlineCoverFetcher>>();
        var fetcher = new OnlineCoverFetcher(fetcherLogger);
        return new CoverImageProvider(logger, fetcher, pdfiumNativeProbe);
    }

    // ---------------------------------------------------------------------
    // Issue #11 — the KEY regression test.
    // ---------------------------------------------------------------------

    [Fact]
    public void IsPdfRenderingAvailable_ProbeFalse_ReturnsFalse_WithoutTouchingPdfToImage()
    {
        // REGRESSION for issue #11: when pdfium is not loadable the method must report
        // "unavailable" by consulting ONLY the injected probe. The old code reached this
        // verdict by calling Conversion.SaveJpeg, which allocates a finalizable PdfLibrary
        // whose throwing ctor later crashed the host on the finalizer thread. With the seam,
        // a false probe yields false and PDFtoImage is never invoked — so no PdfLibrary is
        // ever constructed and no orphaned finalizer can exist.
        var provider = CreateProvider(() => false);

        Assert.False(provider.IsPdfRenderingAvailable());
    }

    [Fact]
    public void IsPdfRenderingAvailable_ProbeTrue_ReturnsTrue()
    {
        var provider = CreateProvider(() => true);

        Assert.True(provider.IsPdfRenderingAvailable());
    }

    [Fact]
    public void IsPdfRenderingAvailable_CachesResult_ProbeInvokedOnce()
    {
        // The result is computed once and cached for the lifetime of the singleton.
        // Repeated calls must NOT re-run the probe.
        var probeCount = 0;
        Func<bool> probe = () =>
        {
            probeCount++;
            return false;
        };

        var provider = CreateProvider(probe);

        var first = provider.IsPdfRenderingAvailable();
        var second = provider.IsPdfRenderingAvailable();
        var third = provider.IsPdfRenderingAvailable();

        Assert.False(first);
        Assert.False(second);
        Assert.False(third);
        Assert.Equal(1, probeCount); // computed exactly once, then cached
    }

    [Fact]
    public void IsPdfRenderingAvailable_ConcurrentCallers_ProbeInvokedExactlyOnce()
    {
        // Guards the double-checked lock in IsPdfRenderingAvailable: even under heavy
        // concurrent first-touch, the probe must run exactly once and every caller must
        // observe the same cached result. A barrier releases all tasks simultaneously to
        // maximise the race window, and the probe sleeps briefly so a broken lock would
        // let a second caller slip in before the cache is populated.
        const int taskCount = 16;
        var probeCount = 0;
        using var barrier = new Barrier(taskCount);

        Func<bool> probe = () =>
        {
            Interlocked.Increment(ref probeCount);
            Thread.Sleep(25); // widen the window an unsafe lock would expose
            return false;
        };

        var provider = CreateProvider(probe);

        var results = new bool[taskCount];
        Parallel.For(0, taskCount, i =>
        {
            barrier.SignalAndWait(); // all callers hit the method at once
            results[i] = provider.IsPdfRenderingAvailable();
        });

        Assert.Equal(1, probeCount);          // double-checked lock held — probe ran once
        Assert.All(results, r => Assert.False(r)); // every caller saw the same cached false
    }

    [Fact]
    public void IsPdfRenderingAvailable_ProbeThrows_ReturnsFalse_AndDoesNotPropagate()
    {
        // The production code wraps the probe in try/catch so a probe failure can never
        // escalate the way the original render-probe did (DllNotFoundException from
        // PDFtoImage). A throwing probe must be swallowed and treated as "unavailable".
        var provider = CreateProvider(() => throw new DllNotFoundException("pdfium not found"));

        var available = provider.IsPdfRenderingAvailable();

        Assert.False(available);
    }

    [Fact]
    public void IsPdfRenderingAvailable_ProbeThrows_ResultIsCached()
    {
        // A thrown probe still yields a cached "false"; it must not be retried on every call.
        var probeCount = 0;
        Func<bool> probe = () =>
        {
            probeCount++;
            throw new DllNotFoundException("pdfium not found");
        };

        var provider = CreateProvider(probe);

        Assert.False(provider.IsPdfRenderingAvailable());
        Assert.False(provider.IsPdfRenderingAvailable());
        Assert.Equal(1, probeCount);
    }

    // ---------------------------------------------------------------------
    // GetImage end-to-end on a .pdf when pdfium is unavailable (deterministic).
    // ---------------------------------------------------------------------

    [Fact]
    public async Task GetImage_PdfFile_WhenPdfiumUnavailable_ReturnsNoImage()
    {
        // Same intent as before but driven deterministically through the seam: a .pdf with a
        // false probe must skip PDF rendering entirely (no PdfLibrary constructed — issue #11),
        // fall through to the online fetcher (null-safe when Plugin.Instance is null), and
        // never throw.
        var provider = CreateProvider(() => false);
        var tmpFile = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}.pdf");
        File.WriteAllText(tmpFile, "%PDF-1.0 dummy");

        try
        {
            var item = new Mock<Book>();
            item.SetupGet(i => i.Path).Returns(tmpFile);
            item.SetupGet(i => i.Name).Returns("Test PDF Book");

            var result = await provider.GetImage(item.Object, ImageType.Primary, CancellationToken.None);

            // Falls through to online (no image, since Plugin.Instance is null) — must not
            // throw, and must report no image rather than a partially-rendered cover.
            Assert.False(result.HasImage);
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }

    // ---------------------------------------------------------------------
    // ffmpeg discovery (unchanged — environment dependent, kept passing).
    // ---------------------------------------------------------------------

    [Fact]
    public void GetFfmpegPath_CachesResult()
    {
        var provider = CreateProvider();
        var first = provider.GetFfmpegPath();
        var second = provider.GetFfmpegPath();
        Assert.Equal(first, second);
    }

    [Fact]
    public void GetFfmpegPath_ReturnsStringOrNull()
    {
        var provider = CreateProvider();
        var path = provider.GetFfmpegPath();
        // May be null (no ffmpeg) or a valid path - both are acceptable
        if (path != null)
        {
            Assert.NotEmpty(path);
        }
    }
}
