using Moq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Xunit;

namespace SmartCovers.Tests;

public class CoverImageProviderTests
{
    private CoverImageProvider CreateProvider()
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
}
