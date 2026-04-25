using Moq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Xunit;

namespace SmartCovers.Tests;

public class CoverImageProviderTests
{
    private static CoverImageProvider CreateProvider()
    {
        var logger = Mock.Of<ILogger<CoverImageProvider>>();
        var fetcherLogger = Mock.Of<ILogger<OnlineCoverFetcher>>();
        var fetcher = new OnlineCoverFetcher(fetcherLogger);
        return new CoverImageProvider(logger, fetcher);
    }

    [Fact]
    public void Supports_Book_ReturnsTrue()
    {
        var provider = CreateProvider();
        var book = new Mock<Book>();
        Assert.True(provider.Supports(book.Object));
    }

    [Fact]
    public void Supports_AudioBook_ReturnsTrue()
    {
        var provider = CreateProvider();
        var audiobook = new Mock<AudioBook>();
        Assert.True(provider.Supports(audiobook.Object));
    }

    [Fact]
    public void Supports_Audio_ReturnsTrue()
    {
        var provider = CreateProvider();
        var audio = new Mock<Audio>();
        Assert.True(provider.Supports(audio.Object));
    }

    [Fact]
    public void Supports_MusicAlbum_ReturnsTrue()
    {
        var provider = CreateProvider();
        var album = new Mock<MusicAlbum>();
        Assert.True(provider.Supports(album.Object));
    }

    [Fact]
    public void Supports_OtherItem_ReturnsFalse()
    {
        var provider = CreateProvider();
        var movie = new Mock<BaseItem>();
        Assert.False(provider.Supports(movie.Object));
    }

    [Fact]
    public void GetSupportedImages_ReturnsPrimary()
    {
        var provider = CreateProvider();
        var item = new Mock<Book>();
        var images = provider.GetSupportedImages(item.Object).ToList();

        Assert.Single(images);
        Assert.Equal(ImageType.Primary, images[0]);
    }

    [Fact]
    public void Name_ReturnsSmartCovers()
    {
        var provider = CreateProvider();
        Assert.Equal("SmartCovers", provider.Name);
    }

    [Fact]
    public async Task GetImage_NullPath_ReturnsNoImage()
    {
        // Null path goes directly to GetOnlineCover. Use empty name so title
        // check returns early (avoids real HTTP calls from Plugin.Instance config).
        var provider = CreateProvider();
        var item = new Mock<Audio>();
        item.SetupGet(i => i.Path).Returns((string?)null);
        item.SetupGet(i => i.Name).Returns(string.Empty);

        var result = await provider.GetImage(item.Object, ImageType.Primary, CancellationToken.None);
        Assert.False(result.HasImage);
    }

    [Fact]
    public async Task GetImage_EmptyPath_ReturnsNoImage()
    {
        var provider = CreateProvider();
        var item = new Mock<Audio>();
        item.SetupGet(i => i.Path).Returns(string.Empty);
        item.SetupGet(i => i.Name).Returns(string.Empty);

        var result = await provider.GetImage(item.Object, ImageType.Primary, CancellationToken.None);
        Assert.False(result.HasImage);
    }

    [Fact]
    public async Task GetImage_UnknownExtension_ReturnsNoImage()
    {
        var provider = CreateProvider();
        var tmpFile = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}.xyz");
        File.WriteAllText(tmpFile, "dummy");

        try
        {
            // Use MusicAlbum to avoid online fallback (Books fall through to online)
            var item = new Mock<MusicAlbum>();
            item.SetupGet(i => i.Path).Returns(tmpFile);
            item.SetupGet(i => i.Name).Returns("Test");

            var result = await provider.GetImage(item.Object, ImageType.Primary, CancellationToken.None);
            Assert.False(result.HasImage);
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }

    [Fact]
    public async Task GetImage_DirectoryPath_ScansForAudio()
    {
        var provider = CreateProvider();
        var tmpDir = Path.Combine(Path.GetTempPath(), $"smartcovers-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        try
        {
            // Empty directory - no audio files, no cover images
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
    public async Task GetImage_AudioExtension_NoFfmpeg_ReturnsNoImage()
    {
        var provider = CreateProvider();
        var tmpFile = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}.mp3");
        File.WriteAllBytes(tmpFile, new byte[100]);

        try
        {
            var item = new Mock<Audio>();
            item.SetupGet(i => i.Path).Returns(tmpFile);
            item.SetupGet(i => i.Name).Returns("Test Audio");

            var result = await provider.GetImage(item.Object, ImageType.Primary, CancellationToken.None);
            // Audio items don't fall through to online cover fetch
            Assert.False(result.HasImage);
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }

    [Fact]
    public async Task GetImage_MusicAlbum_NoOnlineFallback()
    {
        // MusicAlbum should NOT try online cover fetch (it's book-only)
        var provider = CreateProvider();
        var tmpFile = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}.xyz");
        File.WriteAllText(tmpFile, "dummy");

        try
        {
            var item = new Mock<MusicAlbum>();
            item.SetupGet(i => i.Path).Returns(tmpFile);
            item.SetupGet(i => i.Name).Returns("Test Album");

            var result = await provider.GetImage(item.Object, ImageType.Primary, CancellationToken.None);
            Assert.False(result.HasImage);
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }
}
