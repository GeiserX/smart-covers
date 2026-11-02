using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using Moq;
using Xunit;

namespace SmartCovers.Tests;

/// <summary>
/// The parser against the names of real books and audiobooks that were left with
/// no cover: Anna's Archive downloads, text-only OCR rebuilds, and audiobook
/// folders and files named in a dozen different ways.
/// </summary>
public class RealLibraryNamesTests
{
    private static BaseItem Named(string name)
    {
        var mock = new Mock<BaseItem>();
        mock.SetupGet(m => m.Name).Returns(name);
        mock.SetupGet(m => m.Path).Returns((string)null!);
        return mock.Object;
    }

    // The 8 audiobook folders whose tracks had no cover. The track itself is "01".
    [Theory]
    [InlineData("Hacia la Fundación (mp3) - Isaac Asimov 1993", "Hacia la Fundación", "Isaac Asimov")]
    [InlineData("La estación de las flores en llamas (mp3) Sarah Lark 2013", "La estación de las flores en llamas", "Sarah Lark")]
    [InlineData("La búsqueda de los elementos(mp3) Isaac Asimov 1983", "La búsqueda de los elementos", "Isaac Asimov")]
    [InlineData("Build, An Unorthodox Guide to Making Things Worth Making", "Build, An Unorthodox Guide to Making Things Worth Making", null)]
    [InlineData("Douglas Stone, Bruce Patton, and Sheila Heen - Difficult Conversations", "Difficult Conversations", "Douglas Stone")]
    [InlineData("[Audiolibro mp3]  Cabo Trafalgar- Arturo Pérez-Reverte 2004", "Cabo Trafalgar", "Arturo Pérez-Reverte")]
    [InlineData("[Audiolibro mp3]  - Asesinato En El Orient Express- Agatha Christie 1934", "Asesinato En El Orient Express", "Agatha Christie")]
    [InlineData("El ultimo hereje (mp3) Jesús Ávila Granados 2013", "El ultimo hereje", "Jesús Ávila Granados")]
    public void AudiobookFolder_ParsesToTitleAndAuthor(string folder, string title, string? author)
    {
        var (parsedTitle, parsedAuthor) = OnlineCoverFetcher.ParseBookInfo(Named("01"), folder);

        Assert.Equal(title, parsedTitle);
        Assert.Equal(author, parsedAuthor);
    }

    // The 4 single-file audiobooks loose in the library's top folder.
    [Theory]
    [InlineData("Moctezuma y Atahualpa (Mp3-Voz Humana)- Eduardo Matos Moctezuma & Luis Millones Santa Gadea", "Moctezuma y Atahualpa", "Eduardo Matos Moctezuma")]
    [InlineData("Camine Con Gusto  (Mp3-Voz Humana)- The Arthritis Foundation", "Camine Con Gusto", "The Arthritis Foundation")]
    [InlineData("Cuando se apagan las luces (Mp3-Voz Humana) -  Care Santos", "Cuando se apagan las luces", "Care Santos")]
    [InlineData("España, terra incognita - José Manuel García-Margallo & Fernando Eguidazu", "España, terra incognita", "José Manuel García-Margallo")]
    public void AudiobookFile_ParsesToTitleAndFirstAuthor(string name, string title, string author)
    {
        var (parsedTitle, parsedAuthor) = OnlineCoverFetcher.ParseBookInfo(Named(name));

        Assert.Equal(title, parsedTitle);
        Assert.Equal(author, parsedAuthor);
    }

    // The 14 e-books with no cover, as Anna's Archive names them.
    [Theory]
    [InlineData(
        "Archivística en Navarra - [Juan José Martinena Ruiz ___ et -- Navarre (spain), Departamento De Educación Y Cultura_ -- Fuentes para la historia de -- isbn13 9788423523528 -- 0c5de1e782fb34e19d2002acacb38187 -- Anna’s Archive",
        "Archivística en Navarra", "Navarre (spain), Departamento De Educación Y Cultura", "9788423523528", null)]
    [InlineData(
        "Bel-Abbès et son arrondissement (texto OCR, HathiTrust) -- Bastide, L. -- 19a7f9edf65e0a46f55337e89c73545f -- Anna’s Archive",
        "Bel-Abbès et son arrondissement", "Bastide, L.", null, null)]
    [InlineData(
        "Cátalogo de privilegios y mercedes de hidalguía, recopilado -- Archivo General de Simancas; Mariano Alcocer y Martinez -- Spain, 1927 -- Imprenta de -- oclc 15680789 -- be61811db1505cdc419c0e5ff022afdf -- Anna’s Archive",
        "Cátalogo de privilegios y mercedes de hidalguía, recopilado", "Archivo General de Simancas; Mariano Alcocer y Martinez", null, "15680789")]
    [InlineData(
        "Dizionario dei luoghi della Calabria (texto OCR, HathiTrust) -- Valente, Gustavo -- 6cbcdbc0fd26c4de641dcb63658de896 -- Anna’s Archive",
        "Dizionario dei luoghi della Calabria", "Valente, Gustavo", null, null)]
    [InlineData(
        "El arsenal de Cartagena en el siglo XVIII - María Teresa -- Pérez-Crespo Muñoz, María Teresa_ -- Coleccion \"Aula de navegantes\" -- 6, Colección \"Aula -- isbn13 9788497813662 -- 309174209961e9afe761938a8ae520b0 -- Anna’s Archive",
        "El arsenal de Cartagena en el siglo XVIII", "Pérez-Crespo Muñoz, María Teresa", "9788497813662", null)]
    [InlineData(
        "El crecimiento de la población almeriense, 1752-1910 _ una -- Gómez Díaz, Donato_ -- Cuadernos monográficos ;, 18, Cuadernos monográficos -- isbn13 9788486862893 -- 6f776a8d7a55b33e2cdab044daddc35b -- Anna’s Archive",
        "El crecimiento de la población almeriense, 1752-1910: una", "Gómez Díaz, Donato", "9788486862893", null)]
    [InlineData(
        "Entre el deshonor y la miseria. Infancia abandonada en Guipúzcoa y Navarra, siglos XVIII y XIX (texto OCR, HathiTrust) -- Valverde, Lola -- dcabcc567ebfc1db16a3dadac9c903cd -- Anna’s Archive",
        "Entre el deshonor y la miseria. Infancia abandonada en Guipúzcoa y Navarra, siglos XVIII y XIX", "Valverde, Lola", null, null)]
    [InlineData(
        "Executoria de la nobleza, antiguedad y blasones del Valle de -- Goyeneche, Juan de, 1656-1735_ -- Spain, 1685 -- en la Imprenta de Antonio Roman, -- 1bcd9005e5d732369773dca85b068f13 -- Anna’s Archive",
        "Executoria de la nobleza, antiguedad y blasones del Valle de", "Goyeneche, Juan de", null, null)]
    [InlineData(
        "Executorial de hidalguia, nobleza y uso de este escudo de -- Navarra (Reino) -- Spain, 1779 -- en la oficina de D_ Josef Miguel de  Ezquerro, impresor -- 42dc2e1fd8c8e68750e1d802c4ad44c9 -- Anna’s Archive",
        "Executorial de hidalguia, nobleza y uso de este escudo de", "Navarra (Reino)", null, null)]
    [InlineData(
        "La Domination espagnole en Algérie et au Maroc (texto OCR, HathiTrust) -- Froelicher, Émile -- 2d660aadcb074d31166231098b51391d -- Anna’s Archive",
        "La Domination espagnole en Algérie et au Maroc", "Froelicher, Émile", null, null)]
    [InlineData(
        "La Vasconie. Étude historique et critique sur les origines du royaume de Navarre (texto OCR, HathiTrust) -- Jaurgain, Jean de -- 4bfdaada9ec9a566462d610fbea09ad9 -- Anna’s Archive",
        "La Vasconie. Étude historique et critique sur les origines du royaume de Navarre", "Jaurgain, Jean de", null, null)]
    [InlineData(
        "La questione agraria e l'emigrazione in Calabria (texto OCR, HathiTrust) -- Taruffi, Dino -- 35e7225adf6522a273b91a8bc40a691f -- Anna’s Archive",
        "La questione agraria e l'emigrazione in Calabria", "Taruffi, Dino", null, null)]
    [InlineData(
        "Ordenanzas municipales de la Ribera del Júcar _ Villa de Ves -- Almendros Toledo, José Manuel_ -- Spain, 1989 -- Instituto de Estudios Albacetenses de -- isbn13 9788487136023 -- 8feea9e7bfc1c3c4e2848eef8ab8bc83 -- Anna’s Archive",
        "Ordenanzas municipales de la Ribera del Júcar: Villa de Ves", "Almendros Toledo, José Manuel", "9788487136023", null)]
    [InlineData(
        "Ragguaglio di Cosenza e di trent'una sue nobili famiglie (texto OCR, HathiTrust) -- Sambiasi, Girolamo -- ddfd23ef4fa2c00370b807bb36d3f892 -- Anna’s Archive",
        "Ragguaglio di Cosenza e di trent'una sue nobili famiglie", "Sambiasi, Girolamo", null, null)]
    public void AnnasArchiveBook_ParsesToTitleAuthorAndIdentifiers(
        string name, string title, string author, string? isbn, string? oclc)
    {
        var query = OnlineCoverFetcher.ParseBookQuery(Named(name));

        Assert.Equal(title, query.Title);
        Assert.Equal(author, query.Author);
        Assert.Equal(isbn == null ? [] : [isbn], query.Isbns);
        Assert.Equal(oclc, query.Oclc);

        // None of the download site's noise reaches a catalogue or a drawn cover.
        foreach (var text in new[] { query.Title, query.Author! })
        {
            Assert.DoesNotContain("Anna", text, StringComparison.Ordinal);
            Assert.DoesNotContain("OCR", text, StringComparison.Ordinal);
            Assert.DoesNotContain(" -- ", text, StringComparison.Ordinal);
            Assert.DoesNotMatch("[0-9a-f]{32}", text);
        }
    }

    [Fact]
    public void AnnasArchive_AVolumeNumberAfterADash_StaysInTheTitle()
    {
        var query = OnlineCoverFetcher.ParseBookQuery(Named(
            "Catálogo del Archivo Diocesano de Pamplona. Sección Procesos - Tomo 05 (texto OCR, HathiTrust) -- Sales Tirapu, José Luis & Ursúa Irigoyen, Isidoro -- 7f07fb59c414c2cf7025eed0772ca045 -- Anna’s Archive"));

        Assert.Equal("Catálogo del Archivo Diocesano de Pamplona. Sección Procesos - Tomo 05", query.Title);
    }

    [Theory]
    [InlineData("Hacia la Fundación (mp3) - Isaac Asimov 1993")]
    [InlineData("Some -- Title With Dashes")]
    public void NotAnnasArchive_IsLeftToTheOrdinaryParser(string name)
    {
        Assert.False(BookNameParser.TryParseAnnasArchive(name, out _));
    }

    [Fact]
    public void AnAnnasArchiveFileName_GivesItsIsbn_EvenWhenTheNameIsAlreadyClean()
    {
        // Jellyfin replaces Name with the book's own title, but the file keeps the ISBN.
        var mock = new Mock<BaseItem>();
        mock.SetupGet(m => m.Name).Returns("Ordenanzas municipales de la Ribera del Júcar");
        mock.SetupGet(m => m.Path).Returns(
            "/libros/Ordenanzas municipales de la Ribera del Júcar _ Villa de Ves -- Almendros Toledo, José Manuel_ -- Spain, 1989 -- Instituto de Estudios Albacetenses de -- isbn13 9788487136023 -- 8feea9e7bfc1c3c4e2848eef8ab8bc83 -- Anna’s Archive.epub");

        var query = OnlineCoverFetcher.ParseBookQuery(mock.Object);

        Assert.Equal("Ordenanzas municipales de la Ribera del Júcar", query.Title);
        Assert.Equal(["9788487136023"], query.Isbns);
    }

    [Theory]
    [InlineData("9788486862893", "9788486862893")]
    [InlineData("9788486862894", null)]
    [InlineData("978-84-9781-366-2", "9788497813662")]
    [InlineData("0553293354", "0553293354")]
    [InlineData("0553293355", null)]
    [InlineData("urn:uuid:b0e62c59-0000-0000-0000-000000000000", null)]
    public void Isbn_IsAcceptedOnlyWithAValidChecksum(string raw, string? expected)
    {
        Assert.Equal(expected, BookNameParser.NormalizeIsbn(raw));
    }

    [Theory]
    [InlineData("ASIMOV, Isaac", "Isaac Asimov")]
    [InlineData("José Manuel García-Margallo, Fernando Eguidazu", "José Manuel García-Margallo")]
    [InlineData("Isaac Asimov", "Isaac Asimov")]
    public void AuthorTag_SurnameFirstIsFlipped_AndAListGivesItsFirstName(string tag, string expected)
    {
        Assert.Equal(expected, BookNameParser.AuthorFromTag(tag));
    }

    [Fact]
    public void TrackTags_WinOverTheFolderName()
    {
        var track = new AudioBook
        {
            Name = "001. Hacia la Fundación",
            Album = "Hacia la Fundación",
            Artists = ["ASIMOV, Isaac"]
        };

        var query = OnlineCoverFetcher.ParseBookQuery(track, "Hacia la Fundación (mp3) - Isaac Asimov 1993");

        Assert.Equal("Hacia la Fundación", query.Title);
        Assert.Equal("Isaac Asimov", query.Author);
    }

    [Fact]
    public void RootFileTrack_TagAuthorList_GivesTheFirstAuthor()
    {
        var track = new AudioBook
        {
            Name = "España, terra incognita - José Manuel García-Margallo & Fernando Eguidazu",
            Artists = ["José Manuel García-Margallo, Fernando Eguidazu"]
        };

        var (title, author) = OnlineCoverFetcher.ParseBookInfo(track);

        Assert.Equal("España, terra incognita", title);
        Assert.Equal("José Manuel García-Margallo", author);
    }

    [Theory]
    [InlineData("La Vasconie. Étude historique et critique sur les origines du royaume de Navarre", "La Vasconie")]
    [InlineData("Build. An Unorthodox Guide", null)]
    [InlineData("Build, An Unorthodox Guide to Making Things Worth Making", null)]
    [InlineData("Mr. Mercedes: A Novel", "Mr. Mercedes")]
    [InlineData("El crecimiento de la población almeriense, 1752-1910", null)]
    [InlineData("Four Thousand Weeks: Time Management for Mortals", "Four Thousand Weeks")]
    public void MainTitle_DropsASubtitleAfterAColonPeriodOrComma(string title, string? expected)
    {
        Assert.Equal(expected, OnlineCoverFetcher.MainTitleOf(title));
    }

    [Fact]
    public void YearRange_AtTheEndOfATitle_IsKept()
    {
        var (title, _) = OnlineCoverFetcher.ParseBookInfo(Named("El crecimiento de la población almeriense, 1752-1910"));
        Assert.Equal("El crecimiento de la población almeriense, 1752-1910", title);
    }

    [Fact]
    public void TrailingYearAfterADash_IsStillDropped()
    {
        var (title, _) = OnlineCoverFetcher.ParseBookInfo(Named("Some Title - 1999"));
        Assert.Equal("Some Title", title);
    }

    [Fact]
    public void OcrTag_IsStrippedFromAnAlreadyCleanName()
    {
        var (title, _) = OnlineCoverFetcher.ParseBookInfo(
            Named("Ragguaglio di Cosenza e di trent'una sue nobili famiglie (texto OCR, HathiTrust)"));
        Assert.Equal("Ragguaglio di Cosenza e di trent'una sue nobili famiglie", title);
    }

    [Theory]
    [InlineData("Motiva2 - Gracias Sergio", "Gracias Sergio", "Motiva2")]
    [InlineData("The Naked And Famous - Young Blood", "Young Blood", "The Naked And Famous")]
    public void MusicTrack_IsReadAsArtistThenTitle(string name, string title, string artist)
    {
        var (parsedTitle, parsedArtist) = OnlineCoverFetcher.ParseMusicTrack(new Audio { Name = name });

        Assert.Equal(title, parsedTitle);
        Assert.Equal(artist, parsedArtist);
    }
}
