using Moq;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;
using SmartCovers.Configuration;
using Xunit;

namespace SmartCovers.Tests;

/// <summary>
/// A multi-file audiobook is a folder, and every track is its own Jellyfin item
/// named "01" or "Pista 1". These tests pin down that the cover is looked up for
/// the BOOK — and that a shelf or a library root never gets a guessed one.
/// </summary>
public class AudiobookFolderCoverTests : IDisposable
{
    private readonly string _libraryDir;

    public AudiobookFolderCoverTests()
    {
        _libraryDir = Path.Combine(Path.GetTempPath(), $"smartcovers-audiobooks-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_libraryDir);

        // A real library holds several books, so the library root is never mistaken
        // for a single book folder.
        Directory.CreateDirectory(Path.Combine(_libraryDir, "Distant Shore (mp3) Bo Lindqvist 1990"));

        EnsurePluginInstance();
    }

    public void Dispose()
    {
        try { Directory.Delete(_libraryDir, true); } catch { }
        GC.SuppressFinalize(this);
    }

    private void EnsurePluginInstance()
    {
        var appPaths = new Mock<IApplicationPaths>();
        appPaths.SetupGet(p => p.PluginConfigurationsPath).Returns(_libraryDir);
        appPaths.SetupGet(p => p.PluginsPath).Returns(_libraryDir);
        appPaths.SetupGet(p => p.DataPath).Returns(_libraryDir);

        var xmlMock = new Mock<IXmlSerializer>();
        xmlMock.Setup(x => x.DeserializeFromFile(It.IsAny<Type>(), It.IsAny<string>()))
            .Returns(new PluginConfiguration { EnableOnlineCoverFetch = true });

        var plugin = new Plugin(appPaths.Object, xmlMock.Object);
        var configField = plugin.GetType().BaseType!.GetField(
            "_configuration",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        configField?.SetValue(plugin, new PluginConfiguration { EnableOnlineCoverFetch = true });
    }

    private static byte[] FakeJpeg(int size = 5000)
    {
        var data = new byte[size];
        data[0] = 0xFF; data[1] = 0xD8; data[2] = 0xFF; data[3] = 0xE0;
        return data;
    }

    /// <summary>A handler that answers any Open Library search with a usable cover.</summary>
    private static MockHttpHandler HandlerThatAlwaysFindsACover(int coverSize = 5000)
    {
        var handler = new MockHttpHandler();
        handler.AddJsonResponse("openlibrary.org/search.json", new
        {
            docs = new[] { new { title = "Whatever", cover_i = 777 } }
        });
        handler.AddImageResponse("covers.openlibrary.org", FakeJpeg(coverSize));
        return handler;
    }

    private static CoverImageProvider Provider(MockHttpHandler handler)
        => new(
            Mock.Of<ILogger<CoverImageProvider>>(),
            new OnlineCoverFetcher(Mock.Of<ILogger<OnlineCoverFetcher>>(), new HttpClient(handler)),
            () => false);

    private string MakeDir(params string[] parts)
    {
        var path = Path.Combine([_libraryDir, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string MakeTrack(string dir, string fileName)
    {
        var path = Path.Combine(dir, fileName);
        // Not real audio: the point is the path and the name, and embedded-art
        // extraction is expected to find nothing here.
        File.WriteAllBytes(path, new byte[256]);
        return path;
    }

    /// <summary>The search URLs, percent-decoded so titles read as themselves.</summary>
    private static string SearchedTitles(MockHttpHandler handler)
        => string.Join(
            " | ",
            handler.RequestedUrls
                .Where(u => u.Contains("search.json", StringComparison.Ordinal))
                .Select(Uri.UnescapeDataString));

    private static Mock<AudioBook> Track(string path, string name)
    {
        var item = new Mock<AudioBook>();
        item.SetupGet(i => i.Path).Returns(path);
        item.SetupGet(i => i.Name).Returns(name);
        return item;
    }

    [Fact]
    public async Task Track_InBookFolder_SearchesTheBookNotTheTrack()
    {
        var book = MakeDir("Quiet Harbour (mp3) Ana Ruiz 1984");
        var track = MakeTrack(book, "01 - Pista 1.mp3");

        var handler = HandlerThatAlwaysFindsACover();
        var result = await Provider(handler).GetImage(
            Track(track, "Pista  1").Object, ImageType.Primary, CancellationToken.None);

        Assert.True(result.HasImage);
        var searched = SearchedTitles(handler);
        Assert.Contains("Quiet Harbour", searched, StringComparison.Ordinal);
        Assert.DoesNotContain("Pista", searched, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Track_InDiscFolder_SearchesTheBookAboveTheDisc()
    {
        var book = MakeDir("Quiet Harbour (mp3) Ana Ruiz 1984");
        var disc = MakeDir("Quiet Harbour (mp3) Ana Ruiz 1984", "CD3");
        var track = MakeTrack(disc, "01 - Pista 1.mp3");
        _ = book;

        var handler = HandlerThatAlwaysFindsACover();
        var result = await Provider(handler).GetImage(
            Track(track, "Pista  1").Object, ImageType.Primary, CancellationToken.None);

        Assert.True(result.HasImage);
        var searched = SearchedTitles(handler);
        Assert.Contains("Quiet Harbour", searched, StringComparison.Ordinal);
        Assert.DoesNotContain("CD3", searched, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Track_InTitledDiscFolder_SearchesTheBookAboveTheDisc()
    {
        var disc = MakeDir("[Audiolibro mp3] Quiet Harbour - Ana Ruiz 1984", "Quiet Harbour CD 4");
        var track = MakeTrack(disc, "Angel-CD4-001.mp3");

        var handler = HandlerThatAlwaysFindsACover();
        var result = await Provider(handler).GetImage(
            Track(track, "Angel-CD4-001").Object, ImageType.Primary, CancellationToken.None);

        Assert.True(result.HasImage);
        var searched = SearchedTitles(handler);
        Assert.Contains("Quiet Harbour", searched, StringComparison.Ordinal);
        Assert.DoesNotContain("CD 4", searched, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Angel", searched, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LooseTrack_InLibraryRoot_NeverSearchesTheLibraryName()
    {
        var track = MakeTrack(_libraryDir, "Quiet Harbour - Ana Ruiz.mp3");

        var handler = HandlerThatAlwaysFindsACover();
        var result = await Provider(handler).GetImage(
            Track(track, "Quiet Harbour - Ana Ruiz").Object, ImageType.Primary, CancellationToken.None);

        Assert.True(result.HasImage);
        var searched = SearchedTitles(handler);
        Assert.Contains("Quiet Harbour", searched, StringComparison.Ordinal);
        Assert.DoesNotContain("smartcovers-audiobooks", searched, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Supports_PlainFolder_IsTrue_ButNotALibraryRootFolder()
    {
        var provider = Provider(new MockHttpHandler());

        Assert.True(provider.Supports(new Mock<Folder>().Object));
        Assert.False(provider.Supports(new Mock<CollectionFolder>().Object));
        Assert.False(provider.Supports(new Mock<UserRootFolder>().Object));
        Assert.False(provider.Supports(new Mock<AggregateFolder>().Object));
    }

    [Fact]
    public async Task FolderItem_ForOneBook_GetsACover()
    {
        var book = MakeDir("Quiet Harbour (mp3) Ana Ruiz 1984");
        MakeTrack(book, "01 - Pista 1.mp3");

        var item = new Mock<Folder>();
        item.SetupGet(i => i.Path).Returns(book);
        item.SetupGet(i => i.Name).Returns("Quiet Harbour (mp3) Ana Ruiz 1984");

        var handler = HandlerThatAlwaysFindsACover();
        var result = await Provider(handler).GetImage(item.Object, ImageType.Primary, CancellationToken.None);

        Assert.True(result.HasImage);
        Assert.Contains("Quiet Harbour", SearchedTitles(handler), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FolderItem_ForAShelfOfBooks_GetsNothingAndAsksNobody()
    {
        MakeDir("Quiet Harbour (mp3) Ana Ruiz 1984");

        var item = new Mock<Folder>();
        item.SetupGet(i => i.Path).Returns(_libraryDir);
        item.SetupGet(i => i.Name).Returns("Audiobooks");

        var handler = HandlerThatAlwaysFindsACover();
        var result = await Provider(handler).GetImage(item.Object, ImageType.Primary, CancellationToken.None);

        Assert.False(result.HasImage);
        Assert.Empty(handler.RequestedUrls);
    }

    [Fact]
    public async Task FolderItem_HoldingManyBookFiles_IsNeverGivenALookedUpCover()
    {
        var shelf = MakeDir("Some Comic Collection");
        File.WriteAllBytes(Path.Combine(shelf, "Issue 01.cbr"), new byte[256]);
        File.WriteAllBytes(Path.Combine(shelf, "Issue 02.cbr"), new byte[256]);

        var item = new Mock<Folder>();
        item.SetupGet(i => i.Path).Returns(shelf);
        item.SetupGet(i => i.Name).Returns("Some Comic Collection");

        var handler = HandlerThatAlwaysFindsACover();
        var result = await Provider(handler).GetImage(item.Object, ImageType.Primary, CancellationToken.None);

        Assert.False(result.HasImage);
        Assert.Empty(handler.RequestedUrls);
    }

    [Fact]
    public async Task FolderCover_TheOnlyImageInTheFolder_IsTheCover()
    {
        var book = MakeDir("Quiet Harbour (mp3) Ana Ruiz 1984");
        MakeTrack(book, "01 - Pista 1.mp3");
        File.WriteAllBytes(Path.Combine(book, "Harbour.jpg"), FakeJpeg(4321));

        var handler = HandlerThatAlwaysFindsACover();
        var result = await Provider(handler).GetImage(
            Track(Path.Combine(book, "01 - Pista 1.mp3"), "Pista  1").Object,
            ImageType.Primary,
            CancellationToken.None);

        Assert.True(result.HasImage);
        Assert.Equal(ImageFormat.Jpg, result.Format);
        Assert.Equal(4321, result.Stream!.Length);
        Assert.Empty(handler.RequestedUrls);
    }

    [Fact]
    public async Task FolderCover_ImageNamedCoverArt_BeatsAnotherImage()
    {
        var book = MakeDir("Quiet Harbour (mp3) Ana Ruiz 1984");
        MakeTrack(book, "01 - Pista 1.mp3");
        File.WriteAllBytes(Path.Combine(book, "back matter.jpg"), FakeJpeg(9999));
        File.WriteAllBytes(Path.Combine(book, "CoverArt.jpg"), FakeJpeg(4321));

        var handler = HandlerThatAlwaysFindsACover();
        var result = await Provider(handler).GetImage(
            Track(Path.Combine(book, "01 - Pista 1.mp3"), "Pista  1").Object,
            ImageType.Primary,
            CancellationToken.None);

        Assert.True(result.HasImage);
        Assert.Equal(4321, result.Stream!.Length);
    }

    [Fact]
    public async Task FolderCover_SidecarInsideADiscSubfolder_IsFound()
    {
        // The book folder holds only disc folders; the artwork sits with the tracks.
        var book = MakeDir("Quiet Harbour (mp3) Ana Ruiz 1984");
        var disc = MakeDir("Quiet Harbour (mp3) Ana Ruiz 1984", "CD1");
        MakeTrack(disc, "01 - Pista 1.mp3");
        File.WriteAllBytes(Path.Combine(disc, "cover.jpg"), FakeJpeg(4321));

        var item = new Mock<Folder>();
        item.SetupGet(i => i.Path).Returns(book);
        item.SetupGet(i => i.Name).Returns("Quiet Harbour (mp3) Ana Ruiz 1984");

        var handler = HandlerThatAlwaysFindsACover();
        var result = await Provider(handler).GetImage(item.Object, ImageType.Primary, CancellationToken.None);

        Assert.True(result.HasImage);
        Assert.Equal(4321, result.Stream!.Length);
        Assert.Empty(handler.RequestedUrls);
    }

    [Fact]
    public async Task ManyTracksOfOneBook_CostOneLookup()
    {
        var book = MakeDir("Quiet Harbour (mp3) Ana Ruiz 1984");
        var tracks = Enumerable.Range(1, 5)
            .Select(i => MakeTrack(book, $"{i:00} - Pista {i}.mp3"))
            .ToList();

        var handler = HandlerThatAlwaysFindsACover();
        var provider = Provider(handler);

        foreach (var track in tracks)
        {
            var result = await provider.GetImage(
                Track(track, "Pista  1").Object, ImageType.Primary, CancellationToken.None);
            Assert.True(result.HasImage);
        }

        Assert.Equal(1, handler.RequestedUrls.Count(u => u.Contains("search.json", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ManyTracksOfABookWithNoCoverAnywhere_AlsoCostOneLookup()
    {
        var book = MakeDir("Quiet Harbour (mp3) Ana Ruiz 1984");
        var tracks = Enumerable.Range(1, 5)
            .Select(i => MakeTrack(book, $"{i:00} - Pista {i}.mp3"))
            .ToList();

        // Every catalogue comes back empty: the miss must be remembered too, or a
        // 100-track rip runs 100 fruitless lookups.
        var handler = new MockHttpHandler();
        handler.AddJsonResponse("openlibrary.org/search.json", new { docs = Array.Empty<object>() });
        handler.AddJsonResponse("googleapis.com/books", new { totalItems = 0 });

        var provider = Provider(handler);
        var afterFirstTrack = -1;

        foreach (var track in tracks)
        {
            var result = await provider.GetImage(
                Track(track, "Pista  1").Object, ImageType.Primary, CancellationToken.None);
            Assert.False(result.HasImage);

            var searches = handler.RequestedUrls.Count(u => u.Contains("search.json", StringComparison.Ordinal));
            if (afterFirstTrack < 0)
            {
                afterFirstTrack = searches;
                Assert.True(afterFirstTrack > 0, "the first track should actually look the book up");
            }

            // Every track after the first must cost nothing.
            Assert.Equal(afterFirstTrack, searches);
        }
    }

    [Fact]
    public async Task TheCacheIsBounded_AndForgetsTheOldestBook()
    {
        // Five books through a cache that holds four: the first is evicted and has
        // to be looked up again, while the most recent stays free.
        var books = Enumerable.Range(1, 5)
            .Select(i =>
            {
                var dir = MakeDir($"Quiet Harbour {i} (mp3) Ana Ruiz 1984");
                MakeTrack(dir, "01 - Pista 1.mp3");
                return Path.Combine(dir, "01 - Pista 1.mp3");
            })
            .ToList();

        var handler = HandlerThatAlwaysFindsACover();
        var provider = Provider(handler);

        foreach (var track in books)
        {
            Assert.True((await provider.GetImage(
                Track(track, "Pista  1").Object, ImageType.Primary, CancellationToken.None)).HasImage);
        }

        var afterFirstPass = handler.RequestedUrls.Count;

        // The newest book is still cached.
        Assert.True((await provider.GetImage(
            Track(books[4], "Pista  1").Object, ImageType.Primary, CancellationToken.None)).HasImage);
        Assert.Equal(afterFirstPass, handler.RequestedUrls.Count);

        // The oldest was evicted, so it costs a lookup again.
        Assert.True((await provider.GetImage(
            Track(books[0], "Pista  1").Object, ImageType.Primary, CancellationToken.None)).HasImage);
        Assert.True(handler.RequestedUrls.Count > afterFirstPass);
    }

    [Theory]
    [InlineData("2")]
    [InlineData("12")]
    [InlineData("ab")]
    [InlineData("  ")]
    [InlineData("")]
    [InlineData(null)]
    public void IsSearchableTitle_UselessTitles_Rejected(string? title)
        => Assert.False(OnlineCoverFetcher.IsSearchableTitle(title));

    [Theory]
    [InlineData("Quiet Harbour")]
    [InlineData("It 2")]
    public void IsSearchableTitle_RealTitles_Accepted(string title)
        => Assert.True(OnlineCoverFetcher.IsSearchableTitle(title));

    [Fact]
    public async Task BookNamedAfterItsDiscFolder_IsNeverSearchedFor()
    {
        // A PDF whose Jellyfin name is the folder it sits in, "2". Searching a
        // catalogue for "2" returns half a million books and the first one's cover
        // would be shipped as this book's cover.
        var discDir = MakeDir("Fluency Course - Ana Ruiz - 2014", "2");
        var pdf = Path.Combine(discDir, "Fluency Course 2.pdf");
        File.WriteAllBytes(pdf, new byte[256]);

        var item = new Mock<Book>();
        item.SetupGet(i => i.Path).Returns(pdf);
        item.SetupGet(i => i.Name).Returns("2");

        var handler = HandlerThatAlwaysFindsACover();
        var result = await Provider(handler).GetImage(item.Object, ImageType.Primary, CancellationToken.None);

        Assert.False(result.HasImage);
        Assert.Empty(handler.RequestedUrls);
    }
}
