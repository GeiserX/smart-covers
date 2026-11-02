using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using Moq;
using Xunit;

namespace SmartCovers.Tests;

/// <summary>
/// Names that look like something they are not: a title with an ampersand or
/// commas read as a list of authors, a title shaped like "Place, Year", a volume
/// or subtitle after a dash read as a leaked author, a calibre id read as an
/// ISBN, a music title with a dash read as "Artist - Title".
/// </summary>
public class NameParsingGuardTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"smartcovers-guards-{Guid.NewGuid():N}");

    public NameParsingGuardTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
        GC.SuppressFinalize(this);
    }

    private static BaseItem Named(string name)
    {
        var mock = new Mock<BaseItem>();
        mock.SetupGet(m => m.Name).Returns(name);
        mock.SetupGet(m => m.Path).Returns((string)null!);
        return mock.Object;
    }

    [Theory]
    [InlineData("Pride & Prejudice - Jane Austen", "Pride & Prejudice", "Jane Austen")]
    [InlineData("Veni, vidi, vici - Julio César", "Veni, vidi, vici", "Julio César")]
    [InlineData("Sangre, sudor y lágrimas, memorias - Winston Churchill", "Sangre, sudor y lágrimas, memorias", "Winston Churchill")]
    [InlineData("Douglas Stone, Bruce Patton, and Sheila Heen - Difficult Conversations", "Difficult Conversations", "Douglas Stone")]
    [InlineData("Eduardo Matos Moctezuma & Luis Millones - Moctezuma y Atahualpa", "Moctezuma y Atahualpa", "Eduardo Matos Moctezuma")]
    public void OnlyARealListOfPeople_IsTakenForTheAuthors(string name, string title, string author)
    {
        var (parsedTitle, parsedAuthor) = OnlineCoverFetcher.ParseBookInfo(Named(name));

        Assert.Equal(title, parsedTitle);
        Assert.Equal(author, parsedAuthor);
    }

    [Theory]
    [InlineData("Douglas Stone, Bruce Patton, and Sheila Heen", true)]
    [InlineData("José Manuel García-Margallo & Fernando Eguidazu", true)]
    [InlineData("Pride & Prejudice", false)]
    [InlineData("Veni, vidi, vici", false)]
    [InlineData("Sangre, sudor y lágrimas, memorias", false)]
    [InlineData("Crime and Punishment, a novel", false)]
    [InlineData("Difficult Conversations", false)]
    public void AuthorList_NeedsEveryPartToReadAsAFullName(string text, bool expected)
    {
        Assert.Equal(expected, BookNameParser.LooksLikeAuthorList(text));
    }

    [Fact]
    public void AnnasArchive_TitleShapedLikePlaceAndYear_IsKept()
    {
        Assert.True(BookNameParser.TryParseAnnasArchive(
            "Berlín, 1945 -- Antony Beevor -- Barcelona, 2002 -- Crítica -- 0123456789abcdef0123456789abcdef -- Anna’s Archive",
            out var parsed));

        Assert.Equal("Berlín, 1945", parsed!.Title);
        Assert.Equal("Antony Beevor", parsed.Author);
    }

    [Theory]
    [InlineData("Los Miserables - Tomo I -- Victor Hugo -- 0123456789abcdef0123456789abcdef -- Anna’s Archive", "Los Miserables - Tomo I")]
    [InlineData("Los Miserables - Parte Segunda -- Victor Hugo -- 0123456789abcdef0123456789abcdef -- Anna’s Archive", "Los Miserables - Parte Segunda")]
    [InlineData("Star Wars - Una Nueva Esperanza -- George Lucas -- 0123456789abcdef0123456789abcdef -- Anna’s Archive", "Star Wars - Una Nueva Esperanza")]
    [InlineData("Historia de Roma - Libro II -- Tito Livio -- 0123456789abcdef0123456789abcdef -- Anna’s Archive", "Historia de Roma - Libro II")]
    [InlineData("El arsenal de Cartagena en el siglo XVIII - María Teresa -- Pérez-Crespo Muñoz, María Teresa_ -- 0123456789abcdef0123456789abcdef -- Anna’s Archive", "El arsenal de Cartagena en el siglo XVIII")]
    public void AnnasArchive_OnlyATailRepeatingTheAuthor_IsCutFromTheTitle(string name, string title)
    {
        Assert.True(BookNameParser.TryParseAnnasArchive(name, out var parsed));
        Assert.Equal(title, parsed!.Title);
    }

    [Theory]
    [InlineData("<dc:identifier opf:scheme=\"calibre\">0306406152</dc:identifier>")]
    [InlineData("<dc:identifier opf:scheme=\"MOBI-ASIN\">0306406152</dc:identifier>")]
    [InlineData("<dc:identifier opf:scheme=\"OCLC\">9788497813662</dc:identifier>")]
    public void EpubIdentifier_OfAnotherScheme_IsNeverAnIsbn(string identifier)
    {
        // 0306406152 passes the ISBN-10 checksum; the scheme says it is not one.
        var path = EpubOpfTests.MakeEpub(_dir, $"{Guid.NewGuid():N}.epub", "<dc:title>Quiet Harbour</dc:title>" + identifier);

        Assert.Empty(EpubOpf.Read(path)!.Isbns);
    }

    [Fact]
    public void EpubIdentifier_WithAnIsbnUrn_IsReadWhateverTheScheme()
    {
        var path = EpubOpfTests.MakeEpub(_dir, $"{Guid.NewGuid():N}.epub",
            "<dc:title>Quiet Harbour</dc:title><dc:identifier opf:scheme=\"URN\">urn:isbn:0306406152</dc:identifier>");

        Assert.Equal(["0306406152"], EpubOpf.Read(path)!.Isbns);
    }

    [Theory]
    [InlineData("Bohemian Rhapsody - Remastered 2011", new[] { "Queen" }, "Bohemian Rhapsody - Remastered 2011", "Queen")]
    [InlineData("Videotape", new[] { "Radiohead" }, "Videotape", "Radiohead")]
    [InlineData("Radiohead - Videotape", new string[0], "Videotape", "Radiohead")]
    public void MusicTrack_TagsWin_AndOnlyAnUntaggedNameIsSplit(string name, string[] artists, string title, string artist)
    {
        var (parsedTitle, parsedArtist) = OnlineCoverFetcher.ParseMusicTrack(new Audio { Name = name, Artists = artists });

        Assert.Equal(title, parsedTitle);
        Assert.Equal(artist, parsedArtist);
    }

    [Fact]
    public void AlbumTag_CopiedFromTheFolderName_IsReadLikeIt()
    {
        var track = new AudioBook
        {
            Name = "001. Hacia la Fundación",
            Album = "Hacia la Fundación (mp3) - Isaac Asimov 1993"
        };

        var query = OnlineCoverFetcher.ParseBookQuery(track);

        Assert.Equal("Hacia la Fundación", query.Title);
        Assert.Equal("Isaac Asimov", query.Author);
    }

    [Fact]
    public void AlbumTag_WithAPlainSubtitle_StaysWhole()
    {
        var track = new AudioBook
        {
            Name = "01",
            Album = "Star Wars - Una Nueva Esperanza",
            Artists = ["George Lucas"]
        };

        Assert.Equal("Star Wars - Una Nueva Esperanza", OnlineCoverFetcher.ParseBookQuery(track).Title);
    }

    [Fact]
    public void UserAgent_NamesThisBuildsVersion()
    {
        var version = typeof(OnlineCoverFetcher).Assembly.GetName().Version!.ToString();

        Assert.StartsWith($"SmartCovers/{version} ", OnlineCoverFetcher.UserAgent, StringComparison.Ordinal);
        Assert.Contains("github.com/GeiserX/smart-covers", OnlineCoverFetcher.UserAgent, StringComparison.Ordinal);
        Assert.True(new HttpClient().DefaultRequestHeaders.UserAgent.TryParseAdd(OnlineCoverFetcher.UserAgent));
    }
}
