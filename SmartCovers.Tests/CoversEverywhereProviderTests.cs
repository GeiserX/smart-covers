using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Moq;
using SmartCovers.Configuration;
using Xunit;

namespace SmartCovers.Tests;

/// <summary>
/// The provider paths that left real books, audiobooks and music blank: a track
/// that ignored its folder's poster, an image named after the book, and items no
/// catalogue knows, which get a drawn cover when that setting is on.
/// </summary>
public class CoversEverywhereProviderTests : IDisposable
{
    private readonly string _library;
    private readonly CoverOutcomeLog _outcomes = new();
    private readonly Mock<ILibraryManager> _libraryManager = new();

    public CoversEverywhereProviderTests()
    {
        _library = Path.Combine(Path.GetTempPath(), $"smartcovers-everywhere-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_library);

        // A real library holds several books, so its root is never one book.
        Directory.CreateDirectory(Path.Combine(_library, "Distant Shore (mp3) Bo Lindqvist 1990"));

        _libraryManager.Setup(l => l.GetContentType(It.IsAny<BaseItem>())).Returns(CollectionType.books);
        _libraryManager
            .Setup(l => l.GetCollectionFolders(It.IsAny<BaseItem>()))
            .Returns([new CollectionFolder { PhysicalLocationsList = [_library] }]);
    }

    public void Dispose()
    {
        try { Directory.Delete(_library, true); } catch { }
        GC.SuppressFinalize(this);
    }

    private static byte[] Jpeg(int size, byte fill)
    {
        var data = Enumerable.Repeat(fill, size).ToArray();
        data[0] = 0xFF; data[1] = 0xD8; data[2] = 0xFF; data[3] = 0xE0;
        return data;
    }

    private CoverImageProvider Provider(OrderedMockHandler handler, bool online, bool generated)
        => new(
            Mock.Of<ILogger<CoverImageProvider>>(),
            new OnlineCoverFetcher(Mock.Of<ILogger<OnlineCoverFetcher>>(), new HttpClient(handler)) { GoogleBooksApiKeySource = () => null },
            () => false,
            _outcomes,
            _libraryManager.Object)
        {
            ConfigurationSource = () => new PluginConfiguration { EnableOnlineCoverFetch = online, EnableGeneratedCovers = generated },
            GeneratedFontPath = TestFonts.Path
        };

    /// <summary>Open Library and Google Books answer every search with nothing.</summary>
    private static OrderedMockHandler NothingOnline()
    {
        var handler = new OrderedMockHandler();
        handler.WhenUrlContains("openlibrary.org/search", new { docs = Array.Empty<object>() });
        handler.WhenUrlContains("googleapis.com/books", new { totalItems = 0 });
        return handler;
    }

    private string Dir(string name)
    {
        var path = Path.Combine(_library, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string Track(string dir, string name)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, new byte[256]);
        return path;
    }

    private static AudioBook AudioBookItem(string path, string name) => new() { Id = Guid.NewGuid(), Path = path, Name = name };

    private CoverOutcome Outcome(BaseItem item)
    {
        Assert.True(_outcomes.TryTake(item.Id, out var outcome));
        return outcome!;
    }

    private static byte[] Bytes(MediaBrowser.Controller.Providers.DynamicImageResponse response)
    {
        using var ms = new MemoryStream();
        response.Stream.CopyTo(ms);
        return ms.ToArray();
    }

    [Fact]
    public async Task Track_TakesItsBookFoldersPoster_ReadOnceForAllTracks()
    {
        var book = Dir("Hacia la Fundación (mp3) - Isaac Asimov 1993");
        var first = AudioBookItem(Track(book, "001. Hacia la Fundación.mp3"), "001. Hacia la Fundación");
        var second = AudioBookItem(Track(book, "002. Datos Bibliográficos.mp3"), "002. Datos Bibliográficos");

        var poster = Path.Combine(Directory.CreateDirectory(Path.Combine(_library, "..", $"metadata-{Guid.NewGuid():N}")).FullName, "poster.jpg");
        File.WriteAllBytes(poster, Jpeg(4000, 0x11));
        var folder = new Folder { Id = Guid.NewGuid(), Path = book, ImageInfos = [new ItemImageInfo { Type = ImageType.Primary, Path = poster }] };
        _libraryManager.Setup(l => l.FindByPath(book, true)).Returns(folder);

        var provider = Provider(NothingOnline(), online: false, generated: false);
        var a = await provider.GetImage(first, ImageType.Primary, CancellationToken.None);
        var b = await provider.GetImage(second, ImageType.Primary, CancellationToken.None);

        Assert.True(a.HasImage);
        Assert.True(b.HasImage);
        Assert.Equal(File.ReadAllBytes(poster), Bytes(a));
        Assert.Equal(File.ReadAllBytes(poster), Bytes(b));
        Assert.Equal(CoverReason.InheritedFromFolder, Outcome(first).Reason);
        _libraryManager.Verify(l => l.FindByPath(book, true), Times.Once);

        File.Delete(poster);
    }

    [Fact]
    public async Task ImageNamedAfterTheBook_WinsOverAnUntitledOne()
    {
        var book = Dir("[Audiolibro mp3]  Cabo Trafalgar- Arturo Pérez-Reverte 2004");
        var named = Jpeg(3000, 0x22);
        File.WriteAllBytes(Path.Combine(book, "Cabo Trafalgar.jpg"), named);
        File.WriteAllBytes(Path.Combine(book, "untitled.jpg"), Jpeg(3000, 0x33));
        var track = AudioBookItem(Track(book, "01 CABO TRAFALGAR.mp3"), "01 CABO TRAFALGAR");

        var result = await Provider(NothingOnline(), online: false, generated: false)
            .GetImage(track, ImageType.Primary, CancellationToken.None);

        Assert.True(result.HasImage);
        Assert.Equal(named, Bytes(result));
    }

    [Fact]
    public async Task DrawnCoverBesideTheMedia_IsNotTakenForARealOne()
    {
        if (TestFonts.Path is not { } font)
        {
            return;
        }

        var book = Dir("Quiet Harbour (mp3) Ana Ruiz 1984");
        File.WriteAllBytes(Path.Combine(book, "cover.jpg"), GeneratedCover.Render("Quiet Harbour", "Ana Ruiz", 85, font)!);
        var track = AudioBookItem(Track(book, "01.mp3"), "01");

        var result = await Provider(NothingOnline(), online: false, generated: false)
            .GetImage(track, ImageType.Primary, CancellationToken.None);

        Assert.False(result.HasImage);
    }

    private string TextOnlyEpub(string fileName)
        => EpubOpfTests.MakeEpub(_library, fileName, "<dc:identifier>urn:uuid:19a7f9ed-0000-0000-0000-000000000000</dc:identifier>");

    private const string BelAbbes = "Bel-Abbès et son arrondissement (texto OCR, HathiTrust) -- Bastide, L. -- 19a7f9edf65e0a46f55337e89c73545f -- Anna’s Archive";

    [Fact]
    public async Task TextOnlyEpub_SettingOff_StaysBlankWithItsReason()
    {
        var path = TextOnlyEpub(BelAbbes + ".epub");
        var item = new Book { Id = Guid.NewGuid(), Path = path, Name = BelAbbes };

        var result = await Provider(NothingOnline(), online: true, generated: false)
            .GetImage(item, ImageType.Primary, CancellationToken.None);

        Assert.False(result.HasImage);
        var outcome = Outcome(item);
        Assert.Equal(CoverReason.NoOnlineMatch, outcome.Reason);
        Assert.Equal("Bel-Abbès et son arrondissement", outcome.Title);
    }

    [Fact]
    public async Task TextOnlyEpub_SettingOn_GetsADrawnCoverWithTheCleanTitle()
    {
        if (TestFonts.Path == null)
        {
            return;
        }

        var path = TextOnlyEpub(BelAbbes + ".epub");
        var item = new Book { Id = Guid.NewGuid(), Path = path, Name = BelAbbes };

        var result = await Provider(NothingOnline(), online: true, generated: true)
            .GetImage(item, ImageType.Primary, CancellationToken.None);

        Assert.True(result.HasImage);
        Assert.Equal(MediaBrowser.Model.Drawing.ImageFormat.Jpg, result.Format);
        Assert.True(GeneratedCover.IsGenerated(Bytes(result)));

        var outcome = Outcome(item);
        Assert.Equal(CoverReason.Generated, outcome.Reason);
        Assert.Equal("Bel-Abbès et son arrondissement", outcome.Title);
        Assert.Equal("Bastide, L.", outcome.Author);
    }

    [Fact]
    public async Task OpfTitleAndAuthor_WinOverTheFileName()
    {
        var name = "Ordenanzas municipales de la Ribera del Júcar _ Villa de Ves -- Almendros Toledo, José Manuel_ -- Spain, 1989 -- Instituto de Estudios Albacetenses de -- isbn13 9788487136023 -- 8feea9e7bfc1c3c4e2848eef8ab8bc83 -- Anna’s Archive";
        var path = EpubOpfTests.MakeEpub(_library, name + ".epub",
            "<dc:title>Ordenanzas municipales de la Ribera del Júcar: Villa de Ves (1589) y Jorquera (1721)</dc:title>"
            + "<dc:creator>José Manuel Almendros Toledo</dc:creator>");
        var item = new Book { Id = Guid.NewGuid(), Path = path, Name = name };

        var handler = NothingOnline();
        await Provider(handler, online: true, generated: false).GetImage(item, ImageType.Primary, CancellationToken.None);

        var outcome = Outcome(item);
        Assert.Equal("Ordenanzas municipales de la Ribera del Júcar: Villa de Ves (1589) y Jorquera (1721)", outcome.Title);
        Assert.Equal("José Manuel Almendros Toledo", outcome.Author);

        // The file name's ISBN is still asked first.
        Assert.StartsWith("https://openlibrary.org/search.json?isbn=9788487136023&", handler.RequestedUrls[0], StringComparison.Ordinal);
        Assert.Contains(handler.LookupSequence, s => s.Contains("author='José Manuel Almendros Toledo'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BookWithNoAuthorInItsName_UsesTheAuthorJellyfinHolds()
    {
        var item = new Book { Id = Guid.NewGuid(), Name = "El crecimiento de la población almeriense, 1752-1910" };
        _libraryManager
            .Setup(l => l.GetPeople(item))
            .Returns([new PersonInfo { Name = "Donato Gómez Díaz", Type = PersonKind.Author }]);

        await Provider(NothingOnline(), online: true, generated: false).GetImage(item, ImageType.Primary, CancellationToken.None);

        Assert.Equal("Donato Gómez Díaz", Outcome(item).Author);
    }

    [Fact]
    public async Task NetworkError_DrawsNothing_AndIsNotCachedForTheFolder()
    {
        var book = Dir("Archivística en Navarra (mp3) Juan José Martinena Ruiz 1997");
        var first = AudioBookItem(Track(book, "01.mp3"), "01");
        var second = AudioBookItem(Track(book, "02.mp3"), "02");

        var refusing = new OrderedMockHandler();
        refusing.When(url => url.Contains("openlibrary.org", StringComparison.Ordinal), () => new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests));
        refusing.WhenUrlContains("googleapis.com/books", new { totalItems = 0 });

        var provider = Provider(refusing, online: true, generated: true);
        var result = await provider.GetImage(first, ImageType.Primary, CancellationToken.None);

        Assert.False(result.HasImage);
        var outcome = Outcome(first);
        Assert.Equal(CoverReason.NetworkError, outcome.Reason);
        Assert.Contains("429", outcome.Detail, StringComparison.Ordinal);

        // The same provider asks again for the next track: the miss was not cached.
        var countBefore = refusing.RequestedUrls.Count;
        await provider.GetImage(second, ImageType.Primary, CancellationToken.None);
        Assert.True(refusing.RequestedUrls.Count > countBefore);
    }

    [Fact]
    public async Task FolderAudiobook_SettingOn_EveryTrackGetsTheSameDrawnCover_DrawnOnce()
    {
        if (TestFonts.Path == null)
        {
            return;
        }

        var book = Dir("Hacia la Fundación (mp3) - Isaac Asimov 1993");
        var tracks = Enumerable.Range(1, 3)
            .Select(i => AudioBookItem(Track(book, $"00{i}. Pista.mp3"), $"00{i}. Pista"))
            .ToList();

        var provider = Provider(NothingOnline(), online: true, generated: true);
        var covers = new List<byte[]>();
        foreach (var track in tracks)
        {
            var result = await provider.GetImage(track, ImageType.Primary, CancellationToken.None);
            Assert.True(result.HasImage);
            covers.Add(Bytes(result));
        }

        Assert.All(covers, c => Assert.Equal(covers[0], c));
        Assert.Equal(1, provider.GeneratedRenderCount);
        Assert.Equal("Hacia la Fundación", Outcome(tracks[2]).Title);
    }

    [Fact]
    public async Task ItemAlreadyCarryingADrawnCover_KeepsIt_WithoutADrawAgain()
    {
        if (TestFonts.Path is not { } font)
        {
            return;
        }

        var current = Path.Combine(_library, "current-poster.jpg");
        File.WriteAllBytes(current, GeneratedCover.Render("Dizionario dei luoghi della Calabria", "Valente, Gustavo", 85, font)!);
        var item = new Book
        {
            Id = Guid.NewGuid(),
            Name = "Dizionario dei luoghi della Calabria",
            ImageInfos = [new ItemImageInfo { Type = ImageType.Primary, Path = current }]
        };

        var provider = Provider(NothingOnline(), online: true, generated: true);
        var result = await provider.GetImage(item, ImageType.Primary, CancellationToken.None);

        Assert.False(result.HasImage);
        Assert.Equal(CoverReason.KeptGenerated, Outcome(item).Reason);
        Assert.Equal(0, provider.GeneratedRenderCount);
    }

    [Fact]
    public async Task ItemCarryingADrawnCover_KeepsIt_WhenGoogleOnlyHasItsPlaceholder()
    {
        if (TestFonts.Path is not { } font)
        {
            return;
        }

        var current = Path.Combine(_library, "current-poster.jpg");
        File.WriteAllBytes(current, GeneratedCover.Render("Dizionario dei luoghi della Calabria", null, 85, font)!);
        var item = new Book
        {
            Id = Guid.NewGuid(),
            Name = "Dizionario dei luoghi della Calabria",
            ImageInfos = [new ItemImageInfo { Type = ImageType.Primary, Path = current }]
        };

        var placeholder = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "google", "placeholder.png"));
        var handler = new OrderedMockHandler();
        handler.WhenUrlContains("openlibrary.org/search", new { docs = Array.Empty<object>() });
        handler.WhenUrlContains("googleapis.com/books", new
        {
            items = new[]
            {
                new { volumeInfo = new { imageLinks = new { thumbnail = "http://books.google.com/books/content?id=k4RWAAAAYAAJ&zoom=1" } } }
            }
        });
        handler.When(
            url => url.Contains("books.google.com", StringComparison.Ordinal),
            () => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(placeholder) });

        var result = await Provider(handler, online: true, generated: true).GetImage(item, ImageType.Primary, CancellationToken.None);

        Assert.False(result.HasImage);
        Assert.Equal(CoverReason.KeptGenerated, Outcome(item).Reason);
        Assert.Contains(handler.RequestedUrls, u => u.Contains("books.google.com", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ItemCarryingADrawnCover_GetsTheRealOneOnceItExists()
    {
        if (TestFonts.Path is not { } font)
        {
            return;
        }

        var current = Path.Combine(_library, "current-poster.jpg");
        File.WriteAllBytes(current, GeneratedCover.Render("El arsenal de Cartagena en el siglo XVIII", null, 85, font)!);
        var item = new Book
        {
            Id = Guid.NewGuid(),
            Name = "El arsenal de Cartagena en el siglo XVIII",
            ImageInfos = [new ItemImageInfo { Type = ImageType.Primary, Path = current }]
        };

        var handler = new OrderedMockHandler();
        handler.WhenUrlContains("openlibrary.org/search", new { docs = new[] { new { title = "El arsenal", cover_i = 42 } } });
        handler.When(url => url.Contains("covers.openlibrary.org", StringComparison.Ordinal), () => OrderedMockHandler.Jpeg(6000));

        var result = await Provider(handler, online: true, generated: true).GetImage(item, ImageType.Primary, CancellationToken.None);

        Assert.True(result.HasImage);
        Assert.False(GeneratedCover.IsGenerated(Bytes(result)));
        var outcome = Outcome(item);
        Assert.Equal(CoverReason.FoundOnline, outcome.Reason);
        Assert.Equal("Open Library", outcome.Source);
    }

    [Fact]
    public async Task MovieFolder_IsNeverLookedUpNorDrawn()
    {
        _libraryManager.Setup(l => l.GetContentType(It.IsAny<BaseItem>())).Returns(CollectionType.movies);
        var movie = Dir("Desmelenada (2018) [NF WEB-DL 2160p HEVC ES DD+ 5.1][HDO]");
        File.WriteAllBytes(Path.Combine(movie, "Desmelenada.mkv"), new byte[64]);
        var item = new Folder { Id = Guid.NewGuid(), Path = movie, Name = Path.GetFileName(movie) };

        var handler = NothingOnline();
        var result = await Provider(handler, online: true, generated: true).GetImage(item, ImageType.Primary, CancellationToken.None);

        Assert.False(result.HasImage);
        Assert.Empty(handler.RequestedUrls);
        Assert.Equal(CoverReason.NotBookLibrary, Outcome(item).Reason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RootAudiobookFile_UnknownOnline_GetsADrawnCoverOnlyWhenOn(bool generated)
    {
        if (generated && TestFonts.Path == null)
        {
            return;
        }

        var path = Track(_library, "España, terra incognita - José Manuel García-Margallo & Fernando Eguidazu.mp3");
        var item = new AudioBook
        {
            Id = Guid.NewGuid(),
            Path = path,
            Name = "España, terra incognita - José Manuel García-Margallo & Fernando Eguidazu",
            Artists = ["José Manuel García-Margallo, Fernando Eguidazu"]
        };

        var handler = NothingOnline();
        var result = await Provider(handler, online: true, generated: generated).GetImage(item, ImageType.Primary, CancellationToken.None);

        Assert.Equal(generated, result.HasImage);
        var outcome = Outcome(item);
        Assert.Equal(generated ? CoverReason.Generated : CoverReason.NoOnlineMatch, outcome.Reason);
        Assert.Equal("España, terra incognita", outcome.Title);
        Assert.Contains(handler.LookupSequence, s => s.Contains("author='José Manuel García-Margallo'", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LooseMusicTrack_IsNeverLookedUp_AndDrawnOnlyWhenOn(bool generated)
    {
        if (generated && TestFonts.Path == null)
        {
            return;
        }

        _libraryManager.Setup(l => l.GetContentType(It.IsAny<BaseItem>())).Returns(CollectionType.music);
        var item = new Audio { Id = Guid.NewGuid(), Path = Track(_library, "Motiva2 - Gracias Sergio.mp3"), Name = "Motiva2 - Gracias Sergio" };

        var handler = NothingOnline();
        var result = await Provider(handler, online: true, generated: generated).GetImage(item, ImageType.Primary, CancellationToken.None);

        Assert.Empty(handler.RequestedUrls);
        Assert.Equal(generated, result.HasImage);
        var outcome = Outcome(item);
        Assert.Equal("Gracias Sergio", outcome.Title);
        Assert.Equal("Motiva2", outcome.Author);
    }

    [Fact]
    public async Task MusicTrackInAnAlbumFolder_IsNeverDrawn()
    {
        if (TestFonts.Path == null)
        {
            return;
        }

        _libraryManager.Setup(l => l.GetContentType(It.IsAny<BaseItem>())).Returns(CollectionType.music);
        var album = Dir("Some Artist - Some Album");
        var albumItem = new MusicAlbum { Id = Guid.NewGuid(), Path = album, Name = "Some Album" };
        _libraryManager.Setup(l => l.GetItemById(albumItem.Id)).Returns(albumItem);
        var item = new Audio { Id = Guid.NewGuid(), Path = Track(album, "01 - Song.mp3"), Name = "Song", ParentId = albumItem.Id };

        var result = await Provider(NothingOnline(), online: true, generated: true).GetImage(item, ImageType.Primary, CancellationToken.None);

        Assert.False(result.HasImage);
    }

    [Fact]
    public async Task MusicTrackInAFolderOfManyAlbums_GetsADrawnCover_FromItsTags()
    {
        if (TestFonts.Path == null)
        {
            return;
        }

        _libraryManager.Setup(l => l.GetContentType(It.IsAny<BaseItem>())).Returns(CollectionType.music);
        var mixed = Dir("WCYR-Base");
        var folder = new Folder { Id = Guid.NewGuid(), Path = mixed, Name = "WCYR-Base" };
        _libraryManager.Setup(l => l.GetItemById(folder.Id)).Returns(folder);
        var item = new Audio
        {
            Id = Guid.NewGuid(),
            Path = Track(mixed, "Radiohead - Videotape.flac"),
            Name = "Videotape",
            Album = "In Rainbows",
            Artists = ["Radiohead"],
            ParentId = folder.Id
        };

        var handler = NothingOnline();
        var result = await Provider(handler, online: true, generated: true).GetImage(item, ImageType.Primary, CancellationToken.None);

        Assert.True(result.HasImage);
        Assert.Empty(handler.RequestedUrls);
        var outcome = Outcome(item);
        Assert.Equal(CoverReason.Generated, outcome.Reason);
        Assert.Equal("Videotape", outcome.Title);
        Assert.Equal("Radiohead", outcome.Author);
    }

    [Fact]
    public async Task MusicTrackWithAnUnknownParent_IsNeverDrawn()
    {
        if (TestFonts.Path == null)
        {
            return;
        }

        _libraryManager.Setup(l => l.GetContentType(It.IsAny<BaseItem>())).Returns(CollectionType.music);
        var dir = Dir("Somewhere");
        var item = new Audio { Id = Guid.NewGuid(), Path = Track(dir, "Song.mp3"), Name = "Song", ParentId = Guid.NewGuid() };

        var result = await Provider(NothingOnline(), online: true, generated: true).GetImage(item, ImageType.Primary, CancellationToken.None);

        Assert.False(result.HasImage);
    }

    [Fact]
    public async Task RealCover_IsNeverReplacedByADrawnOne()
    {
        if (TestFonts.Path == null)
        {
            return;
        }

        // A cover another provider downloaded earlier; Jellyfin asks again when
        // images are being replaced, and the catalogues find nothing this time.
        var current = Path.Combine(_library, "downloaded-poster.jpg");
        File.WriteAllBytes(current, Jpeg(8000, 0x44));
        var item = new Book
        {
            Id = Guid.NewGuid(),
            Name = "Dizionario dei luoghi della Calabria",
            ImageInfos = [new ItemImageInfo { Type = ImageType.Primary, Path = current }]
        };

        var provider = Provider(NothingOnline(), online: true, generated: true);
        var result = await provider.GetImage(item, ImageType.Primary, CancellationToken.None);

        Assert.False(result.HasImage);
        Assert.Equal(0, provider.GeneratedRenderCount);
        Assert.Equal(CoverReason.NoOnlineMatch, Outcome(item).Reason);
    }
}
