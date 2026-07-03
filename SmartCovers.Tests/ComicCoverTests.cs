using System.IO.Compression;
using Moq;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Xunit;

namespace SmartCovers.Tests;

/// <summary>
/// Covers comic-archive (.cbz/.cbr) extraction. CBZ archives are built at test
/// run time (they are plain ZIPs); CBR archives are committed binary fixtures in
/// <c>Fixtures/</c> because RAR cannot be authored by managed code — see
/// <c>Fixtures/make-fixtures.py</c> for the fixture contract (entry names/sizes).
/// Every test mocks an <c>Audio</c> item: routing is extension-based (so the comic
/// path is exercised regardless of item type), and Audio never triggers the online
/// fallback — a local-extraction regression must fail the test rather than be
/// masked by a live network fetch (<c>Plugin.Instance</c> may be set by unrelated
/// tests running in the same process).
/// </summary>
public class ComicCoverTests
{
    // Fixture contract (make-fixtures.py): the natural-sort winner page-2.jpg is
    // a 6144-byte JPEG; the ordinal-sort trap page-10.jpg is 8192 bytes.
    private const int ExpectedCoverSize = 6144;

    private static readonly byte[] FakeJpeg = CreateFakeJpeg(10_000);

    private static byte[] CreateFakeJpeg(int size)
    {
        var data = new byte[size];
        data[0] = 0xFF;
        data[1] = 0xD8;
        data[2] = 0xFF;
        data[3] = 0xE0;
        return data;
    }

    private static byte[] CreateFakePng(int size)
    {
        var data = new byte[size];
        data[0] = 0x89;
        data[1] = 0x50;
        data[2] = 0x4E;
        data[3] = 0x47;
        return data;
    }

    private static CoverImageProvider CreateProvider()
    {
        var logger = Mock.Of<ILogger<CoverImageProvider>>();
        var fetcherLogger = Mock.Of<ILogger<OnlineCoverFetcher>>();
        var fetcher = new OnlineCoverFetcher(fetcherLogger);
        return new CoverImageProvider(logger, fetcher);
    }

    private static string CreateCbz(string dir, Dictionary<string, byte[]> entries)
    {
        var path = Path.Combine(dir, $"test-{Guid.NewGuid():N}.cbz");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            foreach (var (name, data) in entries)
            {
                var entry = zip.CreateEntry(name);
                using var stream = entry.Open();
                stream.Write(data, 0, data.Length);
            }
        }

        return path;
    }

    private static string FixturePath(string name)
        => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    // ---------------------------------------------------------------- CBZ (zip)

    [Fact]
    public async Task GetImage_Cbz_FirstPageByNaturalOrder_Found()
    {
        var provider = CreateProvider();
        var tmpDir = Path.Combine(Path.GetTempPath(), $"cbz-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        try
        {
            // page-2 must win over page-10 (ordinal string order would put
            // "page-10" first). page-2 is a PNG so the pick is observable via
            // the response format.
            var cbzPath = CreateCbz(tmpDir, new Dictionary<string, byte[]>
            {
                { "page-10.jpg", FakeJpeg },
                { "page-2.png", CreateFakePng(6_000) }
            });

            var item = new Mock<Audio>();
            item.SetupGet(i => i.Path).Returns(cbzPath);
            item.SetupGet(i => i.Name).Returns("Test Comic");

            var result = await provider.GetImage(item.Object, ImageType.Primary, CancellationToken.None);
            Assert.True(result.HasImage);
            Assert.Equal(ImageFormat.Png, result.Format);
        }
        finally
        {
            Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public async Task GetImage_Cbz_CoverByName_PreferredOverFirstPage()
    {
        var provider = CreateProvider();
        var tmpDir = Path.Combine(Path.GetTempPath(), $"cbz-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        try
        {
            // "001.png" sorts first naturally, but the explicitly-named cover
            // (portada.jpg) must win. Formats differ so the pick is observable.
            var cbzPath = CreateCbz(tmpDir, new Dictionary<string, byte[]>
            {
                { "001.png", CreateFakePng(6_000) },
                { "portada.jpg", FakeJpeg }
            });

            var item = new Mock<Audio>();
            item.SetupGet(i => i.Path).Returns(cbzPath);
            item.SetupGet(i => i.Name).Returns("Test Comic");

            var result = await provider.GetImage(item.Object, ImageType.Primary, CancellationToken.None);
            Assert.True(result.HasImage);
            Assert.Equal(ImageFormat.Jpg, result.Format);
        }
        finally
        {
            Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public async Task GetImage_Cbz_JunkEntries_Skipped()
    {
        var provider = CreateProvider();
        var tmpDir = Path.Combine(Path.GetTempPath(), $"cbz-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        try
        {
            // Both junk entries sort before the real page ("." and "_" beat "p")
            // and are JPEGs — the PNG result proves they were filtered.
            var cbzPath = CreateCbz(tmpDir, new Dictionary<string, byte[]>
            {
                { "__MACOSX/page-1.jpg", FakeJpeg },
                { "._page-1.jpg", FakeJpeg },
                { "page-1.png", CreateFakePng(6_000) }
            });

            var item = new Mock<Audio>();
            item.SetupGet(i => i.Path).Returns(cbzPath);
            item.SetupGet(i => i.Name).Returns("Test Comic");

            var result = await provider.GetImage(item.Object, ImageType.Primary, CancellationToken.None);
            Assert.True(result.HasImage);
            Assert.Equal(ImageFormat.Png, result.Format);
        }
        finally
        {
            Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public async Task GetImage_Cbz_TinyImages_Skipped()
    {
        var provider = CreateProvider();
        var tmpDir = Path.Combine(Path.GetTempPath(), $"cbz-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        try
        {
            // "a-icon.jpg" sorts first but is far below the 1000-byte floor.
            var cbzPath = CreateCbz(tmpDir, new Dictionary<string, byte[]>
            {
                { "a-icon.jpg", CreateFakeJpeg(100) },
                { "page-1.png", CreateFakePng(6_000) }
            });

            var item = new Mock<Audio>();
            item.SetupGet(i => i.Path).Returns(cbzPath);
            item.SetupGet(i => i.Name).Returns("Test Comic");

            var result = await provider.GetImage(item.Object, ImageType.Primary, CancellationToken.None);
            Assert.True(result.HasImage);
            Assert.Equal(ImageFormat.Png, result.Format);
        }
        finally
        {
            Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public async Task GetImage_Cbz_TiffEntries_DoNotConsumeCandidateSlots()
    {
        var provider = CreateProvider();
        var tmpDir = Path.Combine(Path.GetTempPath(), $"cbz-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        try
        {
            // TIFF has no magic-byte detector, so a .tiff entry can never ship as
            // a cover — it must be excluded up front rather than burn candidate
            // attempts. More TIFFs than the cap, all sorting before the real page.
            var tiff = new byte[6_000];
            tiff[0] = 0x49; // II*\0 (little-endian TIFF)
            tiff[1] = 0x49;
            tiff[2] = 0x2A;
            tiff[3] = 0x00;

            var entries = new Dictionary<string, byte[]>();
            for (var i = 1; i <= 6; i++)
            {
                entries[$"a-page-{i}.tiff"] = tiff;
            }

            entries["b-page-1.png"] = CreateFakePng(6_000);
            var cbzPath = CreateCbz(tmpDir, entries);

            var item = new Mock<Audio>();
            item.SetupGet(i => i.Path).Returns(cbzPath);
            item.SetupGet(i => i.Name).Returns("Test Comic");

            var result = await provider.GetImage(item.Object, ImageType.Primary, CancellationToken.None);
            Assert.True(result.HasImage);
            Assert.Equal(ImageFormat.Png, result.Format);
        }
        finally
        {
            Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public async Task GetImage_Cbz_ManyTinyImages_DoNotConsumeCandidateSlots()
    {
        var provider = CreateProvider();
        var tmpDir = Path.Combine(Path.GetTempPath(), $"cbz-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        try
        {
            // More tiny junk images than the candidate-attempt cap, all sorting
            // before the real page. They must be filtered out up front — not
            // burn through the candidate slots and leave the real page untried.
            var entries = new Dictionary<string, byte[]>();
            for (var i = 1; i <= 8; i++)
            {
                entries[$"a-junk-{i}.jpg"] = CreateFakeJpeg(100);
            }

            entries["zz-page-1.png"] = CreateFakePng(6_000);
            var cbzPath = CreateCbz(tmpDir, entries);

            var item = new Mock<Audio>();
            item.SetupGet(i => i.Path).Returns(cbzPath);
            item.SetupGet(i => i.Name).Returns("Test Comic");

            var result = await provider.GetImage(item.Object, ImageType.Primary, CancellationToken.None);
            Assert.True(result.HasImage);
            Assert.Equal(ImageFormat.Png, result.Format);
        }
        finally
        {
            Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public async Task GetImage_Cbz_NoImages_ReturnsNoImage()
    {
        var provider = CreateProvider();
        var tmpDir = Path.Combine(Path.GetTempPath(), $"cbz-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        try
        {
            var cbzPath = CreateCbz(tmpDir, new Dictionary<string, byte[]>
            {
                { "ComicInfo.xml", System.Text.Encoding.UTF8.GetBytes("<?xml version='1.0'?><ComicInfo/>") }
            });

            var item = new Mock<Audio>();
            item.SetupGet(i => i.Path).Returns(cbzPath);
            item.SetupGet(i => i.Name).Returns("Test Comic");

            var result = await provider.GetImage(item.Object, ImageType.Primary, CancellationToken.None);
            Assert.False(result.HasImage);
        }
        finally
        {
            Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public async Task GetImage_Cbz_OnlyTinyImages_ReturnsNoImage()
    {
        var provider = CreateProvider();
        var tmpDir = Path.Combine(Path.GetTempPath(), $"cbz-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        try
        {
            var cbzPath = CreateCbz(tmpDir, new Dictionary<string, byte[]>
            {
                { "icon.jpg", CreateFakeJpeg(100) }
            });

            var item = new Mock<Audio>();
            item.SetupGet(i => i.Path).Returns(cbzPath);
            item.SetupGet(i => i.Name).Returns("Test Comic");

            var result = await provider.GetImage(item.Object, ImageType.Primary, CancellationToken.None);
            Assert.False(result.HasImage);
        }
        finally
        {
            Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public async Task GetImage_Cbz_ImageExtensionButGarbageBytes_TriesNextCandidate()
    {
        var provider = CreateProvider();
        var tmpDir = Path.Combine(Path.GetTempPath(), $"cbz-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        try
        {
            // First candidate has an image extension but no recognizable magic
            // bytes; the extractor must move on to the real page.
            var garbage = new byte[6_000];
            garbage[0] = 0x12;
            garbage[1] = 0x34;

            var cbzPath = CreateCbz(tmpDir, new Dictionary<string, byte[]>
            {
                { "page-1.jpg", garbage },
                { "page-2.png", CreateFakePng(6_000) }
            });

            var item = new Mock<Audio>();
            item.SetupGet(i => i.Path).Returns(cbzPath);
            item.SetupGet(i => i.Name).Returns("Test Comic");

            var result = await provider.GetImage(item.Object, ImageType.Primary, CancellationToken.None);
            Assert.True(result.HasImage);
            Assert.Equal(ImageFormat.Png, result.Format);
        }
        finally
        {
            Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public async Task GetImage_Cbz_CorruptArchive_ReturnsNoImage()
    {
        var provider = CreateProvider();
        var tmpDir = Path.Combine(Path.GetTempPath(), $"cbz-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        try
        {
            var cbzPath = Path.Combine(tmpDir, "corrupt.cbz");
            File.WriteAllText(cbzPath, "this is not a valid archive");

            var item = new Mock<Audio>();
            item.SetupGet(i => i.Path).Returns(cbzPath);
            item.SetupGet(i => i.Name).Returns("Test Comic");

            var result = await provider.GetImage(item.Object, ImageType.Primary, CancellationToken.None);
            Assert.False(result.HasImage);
        }
        finally
        {
            Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public async Task GetImage_Cbr_NonexistentFile_ReturnsNoImage()
    {
        var provider = CreateProvider();
        var item = new Mock<Audio>();
        item.SetupGet(i => i.Path).Returns("/nonexistent/path/comic.cbr");
        item.SetupGet(i => i.Name).Returns("Test Comic");

        var result = await provider.GetImage(item.Object, ImageType.Primary, CancellationToken.None);
        Assert.False(result.HasImage);
    }

    // ------------------------------------------------------- CBR (rar fixtures)

    [Fact]
    public async Task GetImage_Cbr_Rar4_FirstPageByNaturalOrder_Found()
    {
        var provider = CreateProvider();

        var item = new Mock<Audio>();
        item.SetupGet(i => i.Path).Returns(FixturePath("first-page-rar4.cbr"));
        item.SetupGet(i => i.Name).Returns("Test Comic");

        var result = await provider.GetImage(item.Object, ImageType.Primary, CancellationToken.None);
        Assert.True(result.HasImage);
        Assert.Equal(ImageFormat.Jpg, result.Format);
        Assert.NotNull(result.Stream);
        Assert.Equal(ExpectedCoverSize, result.Stream!.Length);
    }

    [Fact]
    public async Task GetImage_Cbr_Rar5_FirstPageByNaturalOrder_Found()
    {
        var provider = CreateProvider();

        var item = new Mock<Audio>();
        item.SetupGet(i => i.Path).Returns(FixturePath("first-page-rar5.cbr"));
        item.SetupGet(i => i.Name).Returns("Test Comic");

        var result = await provider.GetImage(item.Object, ImageType.Primary, CancellationToken.None);
        Assert.True(result.HasImage);
        Assert.Equal(ImageFormat.Jpg, result.Format);
        Assert.NotNull(result.Stream);
        Assert.Equal(ExpectedCoverSize, result.Stream!.Length);
    }

    [Fact]
    public async Task GetImage_Cbr_SolidRar5_ExtractsAcrossPrecedingEntries()
    {
        var provider = CreateProvider();

        // In the solid fixture page-2.jpg is NOT the first physical entry, so
        // extraction must decompress across the entries stored before it.
        var item = new Mock<Audio>();
        item.SetupGet(i => i.Path).Returns(FixturePath("solid-rar5.cbr"));
        item.SetupGet(i => i.Name).Returns("Test Comic");

        var result = await provider.GetImage(item.Object, ImageType.Primary, CancellationToken.None);
        Assert.True(result.HasImage);
        Assert.Equal(ImageFormat.Jpg, result.Format);
        Assert.NotNull(result.Stream);
        Assert.Equal(ExpectedCoverSize, result.Stream!.Length);
    }

    [Fact]
    public async Task GetImage_Cbr_ActuallyZip_SniffedAndExtracted()
    {
        var provider = CreateProvider();

        var item = new Mock<Audio>();
        item.SetupGet(i => i.Path).Returns(FixturePath("zip-as-cbr.cbr"));
        item.SetupGet(i => i.Name).Returns("Test Comic");

        var result = await provider.GetImage(item.Object, ImageType.Primary, CancellationToken.None);
        Assert.True(result.HasImage);
        Assert.Equal(ImageFormat.Jpg, result.Format);
        Assert.NotNull(result.Stream);
        Assert.Equal(ExpectedCoverSize, result.Stream!.Length);
    }

    [Fact]
    public async Task GetImage_Cbz_ActuallyRar_SniffedAndExtracted()
    {
        var provider = CreateProvider();

        var item = new Mock<Audio>();
        item.SetupGet(i => i.Path).Returns(FixturePath("rar-as-cbz.cbz"));
        item.SetupGet(i => i.Name).Returns("Test Comic");

        var result = await provider.GetImage(item.Object, ImageType.Primary, CancellationToken.None);
        Assert.True(result.HasImage);
        Assert.Equal(ImageFormat.Jpg, result.Format);
        Assert.NotNull(result.Stream);
        Assert.Equal(ExpectedCoverSize, result.Stream!.Length);
    }
}
