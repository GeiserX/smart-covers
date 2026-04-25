using Moq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Xunit;

namespace SmartCovers.Tests;

public class SidecarImageTests
{
    private static readonly byte[] FakeJpeg = CreateFakeImage(0xFF, 0xD8, 0xFF, 0xE0, 10_000);
    private static readonly byte[] FakePng = CreateFakeImage(0x89, 0x50, 0x4E, 0x47, 10_000);

    private static byte[] CreateFakeImage(byte b0, byte b1, byte b2, byte b3, int size)
    {
        var data = new byte[size];
        data[0] = b0; data[1] = b1; data[2] = b2; data[3] = b3;
        return data;
    }

    private static CoverImageProvider CreateProvider()
    {
        var logger = Mock.Of<ILogger<CoverImageProvider>>();
        var fetcherLogger = Mock.Of<ILogger<OnlineCoverFetcher>>();
        var fetcher = new OnlineCoverFetcher(fetcherLogger);
        return new CoverImageProvider(logger, fetcher);
    }

    [Fact]
    public async Task GetImage_AudioWithSidecar_CoverJpg_Found()
    {
        var provider = CreateProvider();
        var tmpDir = Path.Combine(Path.GetTempPath(), $"sidecar-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        try
        {
            var audioPath = Path.Combine(tmpDir, "audiobook.m4b");
            File.WriteAllBytes(audioPath, new byte[100]);

            // Place a cover.jpg sidecar
            File.WriteAllBytes(Path.Combine(tmpDir, "cover.jpg"), FakeJpeg);

            var item = new Mock<Audio>();
            item.SetupGet(i => i.Path).Returns(audioPath);
            item.SetupGet(i => i.Name).Returns("Test Audio");

            var result = await provider.GetImage(item.Object, ImageType.Primary, CancellationToken.None);
            Assert.True(result.HasImage);
        }
        finally
        {
            Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public async Task GetImage_AudioWithSidecar_SameBaseName_Found()
    {
        var provider = CreateProvider();
        var tmpDir = Path.Combine(Path.GetTempPath(), $"sidecar-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        try
        {
            var audioPath = Path.Combine(tmpDir, "my-song.mp3");
            File.WriteAllBytes(audioPath, new byte[100]);

            // Sidecar with same base name
            File.WriteAllBytes(Path.Combine(tmpDir, "my-song.jpg"), FakeJpeg);

            var item = new Mock<Audio>();
            item.SetupGet(i => i.Path).Returns(audioPath);
            item.SetupGet(i => i.Name).Returns("My Song");

            var result = await provider.GetImage(item.Object, ImageType.Primary, CancellationToken.None);
            Assert.True(result.HasImage);
        }
        finally
        {
            Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public async Task GetImage_AudioWithSidecar_FolderJpg_Found()
    {
        var provider = CreateProvider();
        var tmpDir = Path.Combine(Path.GetTempPath(), $"sidecar-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        try
        {
            var audioPath = Path.Combine(tmpDir, "track.flac");
            File.WriteAllBytes(audioPath, new byte[100]);

            File.WriteAllBytes(Path.Combine(tmpDir, "folder.jpg"), FakeJpeg);

            var item = new Mock<Audio>();
            item.SetupGet(i => i.Path).Returns(audioPath);
            item.SetupGet(i => i.Name).Returns("Track");

            var result = await provider.GetImage(item.Object, ImageType.Primary, CancellationToken.None);
            Assert.True(result.HasImage);
        }
        finally
        {
            Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public async Task GetImage_AudioWithSidecar_FrontPng_Found()
    {
        var provider = CreateProvider();
        var tmpDir = Path.Combine(Path.GetTempPath(), $"sidecar-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        try
        {
            var audioPath = Path.Combine(tmpDir, "track.ogg");
            File.WriteAllBytes(audioPath, new byte[100]);

            File.WriteAllBytes(Path.Combine(tmpDir, "front.png"), FakePng);

            var item = new Mock<Audio>();
            item.SetupGet(i => i.Path).Returns(audioPath);
            item.SetupGet(i => i.Name).Returns("Track");

            var result = await provider.GetImage(item.Object, ImageType.Primary, CancellationToken.None);
            Assert.True(result.HasImage);
        }
        finally
        {
            Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public async Task GetImage_AudioWithSidecar_PosterWebp_Found()
    {
        var provider = CreateProvider();
        var tmpDir = Path.Combine(Path.GetTempPath(), $"sidecar-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        try
        {
            var audioPath = Path.Combine(tmpDir, "track.wav");
            File.WriteAllBytes(audioPath, new byte[100]);

            var webpData = new byte[10_000];
            webpData[0] = 0x52; webpData[1] = 0x49; webpData[2] = 0x46; webpData[3] = 0x46;
            webpData[8] = 0x57; webpData[9] = 0x45; webpData[10] = 0x42; webpData[11] = 0x50;
            File.WriteAllBytes(Path.Combine(tmpDir, "poster.webp"), webpData);

            var item = new Mock<Audio>();
            item.SetupGet(i => i.Path).Returns(audioPath);
            item.SetupGet(i => i.Name).Returns("Track");

            var result = await provider.GetImage(item.Object, ImageType.Primary, CancellationToken.None);
            Assert.True(result.HasImage);
        }
        finally
        {
            Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public async Task GetImage_AudioWithSidecar_TooSmall_Skipped()
    {
        var provider = CreateProvider();
        var tmpDir = Path.Combine(Path.GetTempPath(), $"sidecar-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        try
        {
            var audioPath = Path.Combine(tmpDir, "track.aac");
            File.WriteAllBytes(audioPath, new byte[100]);

            // Sidecar exists but is too small (<1000 bytes)
            File.WriteAllBytes(Path.Combine(tmpDir, "cover.jpg"), new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00 });

            var item = new Mock<Audio>();
            item.SetupGet(i => i.Path).Returns(audioPath);
            item.SetupGet(i => i.Name).Returns("Track");

            var result = await provider.GetImage(item.Object, ImageType.Primary, CancellationToken.None);
            Assert.False(result.HasImage);
        }
        finally
        {
            Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public async Task GetImage_AudioWithSidecar_UnrecognizedFormat_Skipped()
    {
        var provider = CreateProvider();
        var tmpDir = Path.Combine(Path.GetTempPath(), $"sidecar-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        try
        {
            var audioPath = Path.Combine(tmpDir, "track.wma");
            File.WriteAllBytes(audioPath, new byte[100]);

            // Large file but unknown image format
            var unknownData = new byte[10_000];
            unknownData[0] = 0x01; unknownData[1] = 0x02;
            File.WriteAllBytes(Path.Combine(tmpDir, "cover.jpg"), unknownData);

            var item = new Mock<Audio>();
            item.SetupGet(i => i.Path).Returns(audioPath);
            item.SetupGet(i => i.Name).Returns("Track");

            var result = await provider.GetImage(item.Object, ImageType.Primary, CancellationToken.None);
            Assert.False(result.HasImage);
        }
        finally
        {
            Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public async Task GetImage_AudioWithSidecar_NonexistentDir_ReturnsNoImage()
    {
        var provider = CreateProvider();
        var item = new Mock<Audio>();
        item.SetupGet(i => i.Path).Returns("/nonexistent/dir/track.opus");
        item.SetupGet(i => i.Name).Returns("Track");

        var result = await provider.GetImage(item.Object, ImageType.Primary, CancellationToken.None);
        Assert.False(result.HasImage);
    }

    [Fact]
    public async Task GetImage_AudioWithSidecar_ImageWithOffset_Handled()
    {
        var provider = CreateProvider();
        var tmpDir = Path.Combine(Path.GetTempPath(), $"sidecar-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        try
        {
            var audioPath = Path.Combine(tmpDir, "track.m4a");
            File.WriteAllBytes(audioPath, new byte[100]);

            // JPEG with leading null padding
            var paddedJpeg = new byte[10_000];
            paddedJpeg[0] = 0x00;
            paddedJpeg[1] = 0x00;
            paddedJpeg[2] = 0xFF;
            paddedJpeg[3] = 0xD8;
            paddedJpeg[4] = 0xFF;
            paddedJpeg[5] = 0xE0;
            File.WriteAllBytes(Path.Combine(tmpDir, "cover.jpg"), paddedJpeg);

            var item = new Mock<Audio>();
            item.SetupGet(i => i.Path).Returns(audioPath);
            item.SetupGet(i => i.Name).Returns("Track");

            var result = await provider.GetImage(item.Object, ImageType.Primary, CancellationToken.None);
            Assert.True(result.HasImage);
        }
        finally
        {
            Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public async Task GetImage_AudioWithSidecar_ThumbJpeg_Found()
    {
        var provider = CreateProvider();
        var tmpDir = Path.Combine(Path.GetTempPath(), $"sidecar-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        try
        {
            var audioPath = Path.Combine(tmpDir, "track.mp3");
            File.WriteAllBytes(audioPath, new byte[100]);

            File.WriteAllBytes(Path.Combine(tmpDir, "thumb.jpeg"), FakeJpeg);

            var item = new Mock<Audio>();
            item.SetupGet(i => i.Path).Returns(audioPath);
            item.SetupGet(i => i.Name).Returns("Track");

            var result = await provider.GetImage(item.Object, ImageType.Primary, CancellationToken.None);
            Assert.True(result.HasImage);
        }
        finally
        {
            Directory.Delete(tmpDir, true);
        }
    }
}
