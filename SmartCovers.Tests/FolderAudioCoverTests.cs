using Moq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Xunit;

namespace SmartCovers.Tests;

public class FolderAudioCoverTests
{
    private static readonly byte[] FakeJpeg = CreateFakeJpeg(10_000);

    private static byte[] CreateFakeJpeg(int size)
    {
        var data = new byte[size];
        data[0] = 0xFF; data[1] = 0xD8; data[2] = 0xFF; data[3] = 0xE0;
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
    public async Task GetImage_FolderWithCoverJpg_Found()
    {
        var provider = CreateProvider();
        var tmpDir = Path.Combine(Path.GetTempPath(), $"folder-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        try
        {
            // Folder with cover.jpg and an audio file
            File.WriteAllBytes(Path.Combine(tmpDir, "cover.jpg"), FakeJpeg);
            File.WriteAllBytes(Path.Combine(tmpDir, "chapter01.mp3"), new byte[100]);

            var item = new Mock<AudioBook>();
            item.SetupGet(i => i.Path).Returns(tmpDir);
            item.SetupGet(i => i.Name).Returns("Test AudioBook");

            var result = await provider.GetImage(item.Object, ImageType.Primary, CancellationToken.None);
            Assert.True(result.HasImage);
        }
        finally
        {
            Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public async Task GetImage_FolderWithFolderJpg_Found()
    {
        var provider = CreateProvider();
        var tmpDir = Path.Combine(Path.GetTempPath(), $"folder-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        try
        {
            File.WriteAllBytes(Path.Combine(tmpDir, "folder.jpg"), FakeJpeg);

            var item = new Mock<AudioBook>();
            item.SetupGet(i => i.Path).Returns(tmpDir);
            item.SetupGet(i => i.Name).Returns("Test AudioBook");

            var result = await provider.GetImage(item.Object, ImageType.Primary, CancellationToken.None);
            Assert.True(result.HasImage);
        }
        finally
        {
            Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public async Task GetImage_FolderWithFrontPng_Found()
    {
        var provider = CreateProvider();
        var tmpDir = Path.Combine(Path.GetTempPath(), $"folder-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        try
        {
            var pngData = new byte[10_000];
            pngData[0] = 0x89; pngData[1] = 0x50; pngData[2] = 0x4E; pngData[3] = 0x47;
            File.WriteAllBytes(Path.Combine(tmpDir, "front.png"), pngData);

            var item = new Mock<AudioBook>();
            item.SetupGet(i => i.Path).Returns(tmpDir);
            item.SetupGet(i => i.Name).Returns("Test AudioBook");

            var result = await provider.GetImage(item.Object, ImageType.Primary, CancellationToken.None);
            Assert.True(result.HasImage);
        }
        finally
        {
            Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public async Task GetImage_FolderWithPosterWebp_Found()
    {
        var provider = CreateProvider();
        var tmpDir = Path.Combine(Path.GetTempPath(), $"folder-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        try
        {
            var webpData = new byte[10_000];
            webpData[0] = 0x52; webpData[1] = 0x49; webpData[2] = 0x46; webpData[3] = 0x46;
            webpData[8] = 0x57; webpData[9] = 0x45; webpData[10] = 0x42; webpData[11] = 0x50;
            File.WriteAllBytes(Path.Combine(tmpDir, "poster.webp"), webpData);

            var item = new Mock<AudioBook>();
            item.SetupGet(i => i.Path).Returns(tmpDir);
            item.SetupGet(i => i.Name).Returns("Test AudioBook");

            var result = await provider.GetImage(item.Object, ImageType.Primary, CancellationToken.None);
            Assert.True(result.HasImage);
        }
        finally
        {
            Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public async Task GetImage_FolderWithThumbJpeg_Found()
    {
        var provider = CreateProvider();
        var tmpDir = Path.Combine(Path.GetTempPath(), $"folder-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        try
        {
            File.WriteAllBytes(Path.Combine(tmpDir, "thumb.jpeg"), FakeJpeg);

            var item = new Mock<AudioBook>();
            item.SetupGet(i => i.Path).Returns(tmpDir);
            item.SetupGet(i => i.Name).Returns("Test AudioBook");

            var result = await provider.GetImage(item.Object, ImageType.Primary, CancellationToken.None);
            Assert.True(result.HasImage);
        }
        finally
        {
            Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public async Task GetImage_FolderWithSmallCover_Skipped()
    {
        var provider = CreateProvider();
        var tmpDir = Path.Combine(Path.GetTempPath(), $"folder-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        try
        {
            // Cover image exists but < 1000 bytes
            File.WriteAllBytes(Path.Combine(tmpDir, "cover.jpg"), new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00 });

            var item = new Mock<AudioBook>();
            item.SetupGet(i => i.Path).Returns(tmpDir);
            item.SetupGet(i => i.Name).Returns("Test AudioBook");

            var result = await provider.GetImage(item.Object, ImageType.Primary, CancellationToken.None);
            Assert.False(result.HasImage);
        }
        finally
        {
            Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public async Task GetImage_FolderWithUnrecognizedCover_Skipped()
    {
        var provider = CreateProvider();
        var tmpDir = Path.Combine(Path.GetTempPath(), $"folder-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        try
        {
            // Large file but unrecognized format
            var unknownData = new byte[10_000];
            unknownData[0] = 0x01; unknownData[1] = 0x02;
            File.WriteAllBytes(Path.Combine(tmpDir, "cover.jpg"), unknownData);

            var item = new Mock<AudioBook>();
            item.SetupGet(i => i.Path).Returns(tmpDir);
            item.SetupGet(i => i.Name).Returns("Test AudioBook");

            var result = await provider.GetImage(item.Object, ImageType.Primary, CancellationToken.None);
            Assert.False(result.HasImage);
        }
        finally
        {
            Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public async Task GetImage_FolderEmpty_NoAudioFiles_ReturnsNoImage()
    {
        var provider = CreateProvider();
        var tmpDir = Path.Combine(Path.GetTempPath(), $"folder-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        try
        {
            var item = new Mock<AudioBook>();
            item.SetupGet(i => i.Path).Returns(tmpDir);
            item.SetupGet(i => i.Name).Returns("Test AudioBook");

            var result = await provider.GetImage(item.Object, ImageType.Primary, CancellationToken.None);
            Assert.False(result.HasImage);
        }
        finally
        {
            Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public async Task GetImage_FolderWithCoverAndOffset_Handled()
    {
        var provider = CreateProvider();
        var tmpDir = Path.Combine(Path.GetTempPath(), $"folder-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        try
        {
            // Cover image with leading null padding
            var paddedJpeg = new byte[10_000];
            paddedJpeg[0] = 0x00; paddedJpeg[1] = 0x00;
            paddedJpeg[2] = 0xFF; paddedJpeg[3] = 0xD8; paddedJpeg[4] = 0xFF; paddedJpeg[5] = 0xE0;
            File.WriteAllBytes(Path.Combine(tmpDir, "cover.jpg"), paddedJpeg);

            var item = new Mock<AudioBook>();
            item.SetupGet(i => i.Path).Returns(tmpDir);
            item.SetupGet(i => i.Name).Returns("Test AudioBook");

            var result = await provider.GetImage(item.Object, ImageType.Primary, CancellationToken.None);
            Assert.True(result.HasImage);
        }
        finally
        {
            Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public async Task GetImage_FolderWithAudioAndSidecar_FallsBackToSidecar()
    {
        var provider = CreateProvider();
        var tmpDir = Path.Combine(Path.GetTempPath(), $"folder-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        try
        {
            // Audio file present but no embedded art (ffmpeg would fail),
            // sidecar next to audio file should be found
            File.WriteAllBytes(Path.Combine(tmpDir, "chapter01.mp3"), new byte[100]);
            File.WriteAllBytes(Path.Combine(tmpDir, "chapter01.jpg"), FakeJpeg);

            var item = new Mock<AudioBook>();
            item.SetupGet(i => i.Path).Returns(tmpDir);
            item.SetupGet(i => i.Name).Returns("Test AudioBook");

            var result = await provider.GetImage(item.Object, ImageType.Primary, CancellationToken.None);
            Assert.True(result.HasImage);
        }
        finally
        {
            Directory.Delete(tmpDir, true);
        }
    }
}
