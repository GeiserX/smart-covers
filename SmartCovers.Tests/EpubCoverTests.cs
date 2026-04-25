using System.IO.Compression;
using Moq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Xunit;

namespace SmartCovers.Tests;

public class EpubCoverTests
{
    // Minimal JPEG: FF D8 FF E0 followed by enough bytes to be valid
    private static readonly byte[] FakeJpeg = CreateFakeJpeg(10_000);
    private static readonly byte[] SmallJpeg = CreateFakeJpeg(100);

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

    private static string CreateEpub(string dir, Dictionary<string, byte[]> entries)
    {
        var path = Path.Combine(dir, $"test-{Guid.NewGuid():N}.epub");
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

    [Fact]
    public async Task GetImage_Epub_CoverByName_Found()
    {
        var provider = CreateProvider();
        var tmpDir = Path.Combine(Path.GetTempPath(), $"epub-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        try
        {
            var epubPath = CreateEpub(tmpDir, new Dictionary<string, byte[]>
            {
                { "OEBPS/Images/cover.jpg", FakeJpeg },
                { "OEBPS/Images/icon.png", SmallJpeg }
            });

            var item = new Mock<Book>();
            item.SetupGet(i => i.Path).Returns(epubPath);
            item.SetupGet(i => i.Name).Returns("Test Book");

            var result = await provider.GetImage(item.Object, ImageType.Primary, CancellationToken.None);
            Assert.True(result.HasImage);
            Assert.NotNull(result.Stream);
        }
        finally
        {
            Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public async Task GetImage_Epub_CoverByPathContains_Found()
    {
        var provider = CreateProvider();
        var tmpDir = Path.Combine(Path.GetTempPath(), $"epub-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        try
        {
            // No file named exactly "cover" but path contains "cover"
            var epubPath = CreateEpub(tmpDir, new Dictionary<string, byte[]>
            {
                { "OEBPS/Images/cover-image.jpg", FakeJpeg },
                { "OEBPS/Images/chapter1.png", CreateFakePng(6000) }
            });

            var item = new Mock<Book>();
            item.SetupGet(i => i.Path).Returns(epubPath);
            item.SetupGet(i => i.Name).Returns("Test Book");

            var result = await provider.GetImage(item.Object, ImageType.Primary, CancellationToken.None);
            Assert.True(result.HasImage);
        }
        finally
        {
            Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public async Task GetImage_Epub_LargestImage_Fallback()
    {
        var provider = CreateProvider();
        var tmpDir = Path.Combine(Path.GetTempPath(), $"epub-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        try
        {
            // No "cover" in name or path, falls back to largest image > 5KB
            var epubPath = CreateEpub(tmpDir, new Dictionary<string, byte[]>
            {
                { "OEBPS/Images/illustration.jpg", FakeJpeg },
                { "OEBPS/Images/small-icon.png", CreateFakePng(100) }
            });

            var item = new Mock<Book>();
            item.SetupGet(i => i.Path).Returns(epubPath);
            item.SetupGet(i => i.Name).Returns("Test Book");

            var result = await provider.GetImage(item.Object, ImageType.Primary, CancellationToken.None);
            Assert.True(result.HasImage);
        }
        finally
        {
            Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public async Task GetImage_Epub_NoImages_ReturnsNoImage()
    {
        var provider = CreateProvider();
        var tmpDir = Path.Combine(Path.GetTempPath(), $"epub-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        try
        {
            var epubPath = CreateEpub(tmpDir, new Dictionary<string, byte[]>
            {
                { "OEBPS/content.html", System.Text.Encoding.UTF8.GetBytes("<html></html>") },
                { "OEBPS/toc.ncx", System.Text.Encoding.UTF8.GetBytes("<?xml version='1.0'?>") }
            });

            // Use Audio to avoid online fallback (Book would try online if Plugin.Instance is set)
            var item = new Mock<Audio>();
            item.SetupGet(i => i.Path).Returns(epubPath);
            item.SetupGet(i => i.Name).Returns("Test Book");

            var result = await provider.GetImage(item.Object, ImageType.Primary, CancellationToken.None);
            Assert.False(result.HasImage);
        }
        finally
        {
            Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public async Task GetImage_Epub_OnlySmallImages_ReturnsNoImage()
    {
        var provider = CreateProvider();
        var tmpDir = Path.Combine(Path.GetTempPath(), $"epub-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        try
        {
            // Images exist but all are < 5KB and none named "cover"
            var epubPath = CreateEpub(tmpDir, new Dictionary<string, byte[]>
            {
                { "OEBPS/Images/bullet.png", CreateFakePng(200) },
                { "OEBPS/Images/spacer.gif", new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x01 } }
            });

            var item = new Mock<Audio>();
            item.SetupGet(i => i.Path).Returns(epubPath);
            item.SetupGet(i => i.Name).Returns("Test Book");

            var result = await provider.GetImage(item.Object, ImageType.Primary, CancellationToken.None);
            Assert.False(result.HasImage);
        }
        finally
        {
            Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public async Task GetImage_Epub_CorruptZip_ReturnsNoImage()
    {
        var provider = CreateProvider();
        var tmpDir = Path.Combine(Path.GetTempPath(), $"epub-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        try
        {
            var epubPath = Path.Combine(tmpDir, "corrupt.epub");
            File.WriteAllText(epubPath, "this is not a valid zip file");

            var item = new Mock<Audio>();
            item.SetupGet(i => i.Path).Returns(epubPath);
            item.SetupGet(i => i.Name).Returns("Test Book");

            var result = await provider.GetImage(item.Object, ImageType.Primary, CancellationToken.None);
            Assert.False(result.HasImage);
        }
        finally
        {
            Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public async Task GetImage_Epub_EmptyEntry_ReturnsNoImage()
    {
        var provider = CreateProvider();
        var tmpDir = Path.Combine(Path.GetTempPath(), $"epub-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        try
        {
            // Cover file exists but is empty
            var epubPath = CreateEpub(tmpDir, new Dictionary<string, byte[]>
            {
                { "OEBPS/Images/cover.jpg", Array.Empty<byte>() }
            });

            var item = new Mock<Audio>();
            item.SetupGet(i => i.Path).Returns(epubPath);
            item.SetupGet(i => i.Name).Returns("Test Book");

            var result = await provider.GetImage(item.Object, ImageType.Primary, CancellationToken.None);
            Assert.False(result.HasImage);
        }
        finally
        {
            Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public async Task GetImage_Epub_PortadaName_Found()
    {
        var provider = CreateProvider();
        var tmpDir = Path.Combine(Path.GetTempPath(), $"epub-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        try
        {
            var epubPath = CreateEpub(tmpDir, new Dictionary<string, byte[]>
            {
                { "OEBPS/Images/portada.jpg", FakeJpeg }
            });

            var item = new Mock<Book>();
            item.SetupGet(i => i.Path).Returns(epubPath);
            item.SetupGet(i => i.Name).Returns("Test Book");

            var result = await provider.GetImage(item.Object, ImageType.Primary, CancellationToken.None);
            Assert.True(result.HasImage);
        }
        finally
        {
            Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public async Task GetImage_Epub_PngCover_SetsCorrectFormat()
    {
        var provider = CreateProvider();
        var tmpDir = Path.Combine(Path.GetTempPath(), $"epub-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        try
        {
            var epubPath = CreateEpub(tmpDir, new Dictionary<string, byte[]>
            {
                { "OEBPS/Images/cover.png", CreateFakePng(10_000) }
            });

            var item = new Mock<Book>();
            item.SetupGet(i => i.Path).Returns(epubPath);
            item.SetupGet(i => i.Name).Returns("Test Book");

            var result = await provider.GetImage(item.Object, ImageType.Primary, CancellationToken.None);
            Assert.True(result.HasImage);
        }
        finally
        {
            Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public async Task GetImage_Epub_WebpCover_SetsCorrectFormat()
    {
        var provider = CreateProvider();
        var tmpDir = Path.Combine(Path.GetTempPath(), $"epub-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        try
        {
            var webpData = new byte[10_000];
            // RIFF....WEBP
            webpData[0] = 0x52; webpData[1] = 0x49; webpData[2] = 0x46; webpData[3] = 0x46;
            webpData[8] = 0x57; webpData[9] = 0x45; webpData[10] = 0x42; webpData[11] = 0x50;

            var epubPath = CreateEpub(tmpDir, new Dictionary<string, byte[]>
            {
                { "OEBPS/Images/cover.webp", webpData }
            });

            var item = new Mock<Book>();
            item.SetupGet(i => i.Path).Returns(epubPath);
            item.SetupGet(i => i.Name).Returns("Test Book");

            var result = await provider.GetImage(item.Object, ImageType.Primary, CancellationToken.None);
            Assert.True(result.HasImage);
        }
        finally
        {
            Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public async Task GetImage_Epub_GifCover_SetsCorrectFormat()
    {
        var provider = CreateProvider();
        var tmpDir = Path.Combine(Path.GetTempPath(), $"epub-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        try
        {
            var gifData = new byte[10_000];
            gifData[0] = 0x47; gifData[1] = 0x49; gifData[2] = 0x46;

            var epubPath = CreateEpub(tmpDir, new Dictionary<string, byte[]>
            {
                { "OEBPS/Images/cover.gif", gifData }
            });

            var item = new Mock<Book>();
            item.SetupGet(i => i.Path).Returns(epubPath);
            item.SetupGet(i => i.Name).Returns("Test Book");

            var result = await provider.GetImage(item.Object, ImageType.Primary, CancellationToken.None);
            Assert.True(result.HasImage);
        }
        finally
        {
            Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public async Task GetImage_Epub_BmpExtension_DefaultsToJpegMime()
    {
        var provider = CreateProvider();
        var tmpDir = Path.Combine(Path.GetTempPath(), $"epub-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        try
        {
            // BMP extension falls through to default "image/jpeg" mime in ExtractZipEntry
            var epubPath = CreateEpub(tmpDir, new Dictionary<string, byte[]>
            {
                { "OEBPS/Images/cover.bmp", FakeJpeg }
            });

            var item = new Mock<Book>();
            item.SetupGet(i => i.Path).Returns(epubPath);
            item.SetupGet(i => i.Name).Returns("Test Book");

            var result = await provider.GetImage(item.Object, ImageType.Primary, CancellationToken.None);
            Assert.True(result.HasImage);
        }
        finally
        {
            Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public async Task GetImage_Epub_FrontCoverName_Found()
    {
        var provider = CreateProvider();
        var tmpDir = Path.Combine(Path.GetTempPath(), $"epub-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        try
        {
            var epubPath = CreateEpub(tmpDir, new Dictionary<string, byte[]>
            {
                { "OEBPS/Images/frontcover.jpg", FakeJpeg }
            });

            var item = new Mock<Book>();
            item.SetupGet(i => i.Path).Returns(epubPath);
            item.SetupGet(i => i.Name).Returns("Test Book");

            var result = await provider.GetImage(item.Object, ImageType.Primary, CancellationToken.None);
            Assert.True(result.HasImage);
        }
        finally
        {
            Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public async Task GetImage_Epub_NonexistentFile_ReturnsNoImage()
    {
        var provider = CreateProvider();
        var item = new Mock<Audio>();
        item.SetupGet(i => i.Path).Returns("/nonexistent/path/book.epub");
        item.SetupGet(i => i.Name).Returns("Test Book");

        var result = await provider.GetImage(item.Object, ImageType.Primary, CancellationToken.None);
        Assert.False(result.HasImage);
    }

    [Fact]
    public async Task GetImage_Epub_TiffExtension_IsImageFile()
    {
        var provider = CreateProvider();
        var tmpDir = Path.Combine(Path.GetTempPath(), $"epub-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        try
        {
            // .tiff is in ImageExtensions but not recognized by DetectImageFormat magic bytes
            // Still should be found as an image file in the EPUB
            var epubPath = CreateEpub(tmpDir, new Dictionary<string, byte[]>
            {
                { "OEBPS/Images/cover.tiff", FakeJpeg }
            });

            var item = new Mock<Book>();
            item.SetupGet(i => i.Path).Returns(epubPath);
            item.SetupGet(i => i.Name).Returns("Test Book");

            var result = await provider.GetImage(item.Object, ImageType.Primary, CancellationToken.None);
            Assert.True(result.HasImage);
        }
        finally
        {
            Directory.Delete(tmpDir, true);
        }
    }
}
