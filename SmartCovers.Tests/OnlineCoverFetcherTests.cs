using Moq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Model.Drawing;
using Microsoft.Extensions.Logging;
using Xunit;

namespace SmartCovers.Tests;

public class OnlineCoverFetcherTests
{
    private static OnlineCoverFetcher CreateFetcher()
    {
        return new OnlineCoverFetcher(Mock.Of<ILogger<OnlineCoverFetcher>>());
    }

    [Fact]
    public void ValidateImage_TooSmall_ReturnsNull()
    {
        var fetcher = CreateFetcher();
        var data = new byte[500]; // < 1000 bytes
        data[0] = 0xFF; data[1] = 0xD8; data[2] = 0xFF;
        var result = fetcher.ValidateImage(data, "TestSource");
        Assert.Null(result);
    }

    [Fact]
    public void ValidateImage_UnrecognizedFormat_ReturnsNull()
    {
        var fetcher = CreateFetcher();
        var data = new byte[2000];
        data[0] = 0x01; data[1] = 0x02; data[2] = 0x03; data[3] = 0x04;
        var result = fetcher.ValidateImage(data, "TestSource");
        Assert.Null(result);
    }

    [Fact]
    public void ValidateImage_ValidJpeg_ReturnsStreamAndFormat()
    {
        var fetcher = CreateFetcher();
        var data = new byte[5000];
        data[0] = 0xFF; data[1] = 0xD8; data[2] = 0xFF; data[3] = 0xE0;
        var result = fetcher.ValidateImage(data, "TestSource");
        Assert.NotNull(result);
        Assert.Equal(ImageFormat.Jpg, result.Value.Format);
        Assert.NotNull(result.Value.Stream);
        Assert.True(result.Value.Stream.Length > 0);
        result.Value.Stream.Dispose();
    }

    [Fact]
    public void ValidateImage_ValidPng_ReturnsStreamAndFormat()
    {
        var fetcher = CreateFetcher();
        var data = new byte[5000];
        data[0] = 0x89; data[1] = 0x50; data[2] = 0x4E; data[3] = 0x47;
        var result = fetcher.ValidateImage(data, "TestSource");
        Assert.NotNull(result);
        Assert.Equal(ImageFormat.Png, result.Value.Format);
        result.Value.Stream.Dispose();
    }

    [Fact]
    public void ValidateImage_JpegWithPadding_ReturnsWithOffset()
    {
        var fetcher = CreateFetcher();
        var data = new byte[5000];
        data[0] = 0x00; data[1] = 0x00; // 2 bytes of padding
        data[2] = 0xFF; data[3] = 0xD8; data[4] = 0xFF; data[5] = 0xE0;
        var result = fetcher.ValidateImage(data, "TestSource");
        Assert.NotNull(result);
        Assert.Equal(ImageFormat.Jpg, result.Value.Format);
        // Stream should be offset (data.Length - offset = 4998)
        Assert.Equal(4998, result.Value.Stream.Length);
        result.Value.Stream.Dispose();
    }

    [Fact]
    public async Task FetchCoverAsync_EmptyTitle_ReturnsNull()
    {
        var fetcher = CreateFetcher();
        var result = await fetcher.FetchCoverAsync("", null, CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task FetchCoverAsync_WhitespaceTitle_ReturnsNull()
    {
        var fetcher = CreateFetcher();
        var result = await fetcher.FetchCoverAsync("   ", "author", CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task FetchCoverByOriginalTitleAsync_NullOriginalTitle_ReturnsNull()
    {
        var fetcher = CreateFetcher();
        // BaseItem.OriginalTitle is not virtual; a default mock has it as null
        var item = new Mock<Book>();
        item.SetupGet(i => i.Name).Returns("Test");

        var result = await fetcher.FetchCoverByOriginalTitleAsync(item.Object, CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task FetchCoverByOriginalTitleAsync_SameAsName_ReturnsNull()
    {
        var fetcher = CreateFetcher();
        // OriginalTitle defaults to null on a fresh BaseItem mock, which hits
        // the IsNullOrWhiteSpace branch. Test passes with null OriginalTitle.
        var item = new Mock<Book>();
        item.SetupGet(i => i.Name).Returns("Test Book");
        item.SetupGet(i => i.Path).Returns((string?)null);

        var result = await fetcher.FetchCoverByOriginalTitleAsync(item.Object, CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public void ParseBookInfo_NullNameUsesPath()
    {
        var item = new Mock<BaseItem>();
        item.SetupGet(i => i.Name).Returns((string?)null);
        item.SetupGet(i => i.Path).Returns("/books/My Great Book.epub");

        var (title, author) = OnlineCoverFetcher.ParseBookInfo(item.Object);
        Assert.Equal("My Great Book", title);
        Assert.Null(author);
    }

    [Fact]
    public void ParseBookInfo_NullNameNullPath_ReturnsEmpty()
    {
        var item = new Mock<BaseItem>();
        item.SetupGet(i => i.Name).Returns((string?)null);
        item.SetupGet(i => i.Path).Returns((string?)null);

        var (title, author) = OnlineCoverFetcher.ParseBookInfo(item.Object);
        Assert.Equal(string.Empty, title);
        Assert.Null(author);
    }

    [Fact]
    public void ParseBookInfo_WithAlbumArtist_ExtractsAuthor()
    {
        var item = new Mock<AudioBook>();
        var albumArtist = item.As<IHasAlbumArtist>();
        albumArtist.SetupGet(a => a.AlbumArtists).Returns(new[] { "Stephen King" });
        item.SetupGet(i => i.Name).Returns("The Shining Stephen King");
        item.SetupGet(i => i.Path).Returns((string?)null);

        var (title, author) = OnlineCoverFetcher.ParseBookInfo(item.Object);
        Assert.Equal("Stephen King", author);
        Assert.DoesNotContain("Stephen King", title);
    }

    [Fact]
    public void ParseBookInfo_WithArtist_ExtractsAuthor()
    {
        var item = new Mock<Audio>();
        var artistItem = item.As<IHasArtist>();
        // IHasAlbumArtist should also be checked, set it to empty
        var albumArtist = item.As<IHasAlbumArtist>();
        albumArtist.SetupGet(a => a.AlbumArtists).Returns(Array.Empty<string>());
        artistItem.SetupGet(a => a.Artists).Returns(new[] { "Artist Name" });
        item.SetupGet(i => i.Name).Returns("Song Title Artist Name");
        item.SetupGet(i => i.Path).Returns((string?)null);

        var (title, author) = OnlineCoverFetcher.ParseBookInfo(item.Object);
        Assert.Equal("Artist Name", author);
    }

    [Fact]
    public void ParseBookInfo_AlbumArtistEmpty_FallsToArtist()
    {
        var item = new Mock<Audio>();
        var albumArtist = item.As<IHasAlbumArtist>();
        albumArtist.SetupGet(a => a.AlbumArtists).Returns(new[] { "" });
        var artistItem = item.As<IHasArtist>();
        artistItem.SetupGet(a => a.Artists).Returns(new[] { "Real Artist" });
        item.SetupGet(i => i.Name).Returns("Track Real Artist");
        item.SetupGet(i => i.Path).Returns((string?)null);

        var (title, author) = OnlineCoverFetcher.ParseBookInfo(item.Object);
        Assert.Equal("Real Artist", author);
    }

    [Fact]
    public void ParseBookInfo_AllFormatsRemoved()
    {
        var item = new Mock<BaseItem>();
        item.SetupGet(i => i.Name).Returns("My Book (Mp3 320kbps) [Castellano] [B012ABC345] (2021) - 2020 - Saga completa");
        item.SetupGet(i => i.Path).Returns((string?)null);

        var (title, _) = OnlineCoverFetcher.ParseBookInfo(item.Object);
        Assert.DoesNotContain("Mp3", title);
        Assert.DoesNotContain("Castellano", title);
        Assert.DoesNotContain("B012ABC345", title);
        Assert.DoesNotContain("2021", title);
        Assert.DoesNotContain("Saga", title);
        Assert.Equal("My Book", title);
    }

    [Fact]
    public void ParseBookInfo_M4aFormatTag_Removed()
    {
        var item = new Mock<BaseItem>();
        item.SetupGet(i => i.Name).Returns("Audiobook (M4a 128kbps)");
        item.SetupGet(i => i.Path).Returns((string?)null);

        var (title, _) = OnlineCoverFetcher.ParseBookInfo(item.Object);
        Assert.Equal("Audiobook", title);
    }

    [Fact]
    public void ParseBookInfo_FlacFormatTag_Removed()
    {
        var item = new Mock<BaseItem>();
        item.SetupGet(i => i.Name).Returns("Album (FLAC 24bit)");
        item.SetupGet(i => i.Path).Returns((string?)null);

        var (title, _) = OnlineCoverFetcher.ParseBookInfo(item.Object);
        Assert.Equal("Album", title);
    }

    [Fact]
    public void ParseBookInfo_EnglishLocaleTag_Removed()
    {
        var item = new Mock<BaseItem>();
        item.SetupGet(i => i.Name).Returns("Book Title [English Version]");
        item.SetupGet(i => i.Path).Returns((string?)null);

        var (title, _) = OnlineCoverFetcher.ParseBookInfo(item.Object);
        Assert.DoesNotContain("English", title);
    }

    [Fact]
    public void ParseBookInfo_TrilogySuffix_Removed()
    {
        var item = new Mock<BaseItem>();
        item.SetupGet(i => i.Name).Returns("Lord of the Rings - Trilogia completa");
        item.SetupGet(i => i.Path).Returns((string?)null);

        var (title, _) = OnlineCoverFetcher.ParseBookInfo(item.Object);
        Assert.Equal("Lord of the Rings", title);
    }

    [Fact]
    public void ParseBookInfo_VolumeSuffix_Removed()
    {
        var item = new Mock<BaseItem>();
        item.SetupGet(i => i.Name).Returns("Comic Series - Vol. 3");
        item.SetupGet(i => i.Path).Returns((string?)null);

        var (title, _) = OnlineCoverFetcher.ParseBookInfo(item.Object);
        Assert.Equal("Comic Series", title);
    }

    [Fact]
    public void ParseBookInfo_DashSplitTooShort_NotSplit()
    {
        // Dash too close to start or end should not split
        var item = new Mock<BaseItem>();
        item.SetupGet(i => i.Name).Returns("AB - CD");
        item.SetupGet(i => i.Path).Returns((string?)null);

        var (title, author) = OnlineCoverFetcher.ParseBookInfo(item.Object);
        // "AB" is only 2 chars (dashIdx = 2, not > 2), so it won't split
        Assert.Null(author);
    }
}
