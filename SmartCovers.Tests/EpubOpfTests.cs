using System.IO.Compression;
using System.Text;
using Xunit;

namespace SmartCovers.Tests;

/// <summary>
/// The EPUB's own package document names the book better than its file name.
/// </summary>
public class EpubOpfTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"smartcovers-opf-{Guid.NewGuid():N}");

    public EpubOpfTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
        GC.SuppressFinalize(this);
    }

    internal static string MakeEpub(string dir, string fileName, string metadata, string manifest = "", Dictionary<string, byte[]>? files = null)
    {
        var path = Path.Combine(dir, fileName);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);

        void Add(string name, string text)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open(), Encoding.UTF8);
            writer.Write(text);
        }

        Add("mimetype", "application/epub+zip");
        Add("META-INF/container.xml",
            "<?xml version=\"1.0\"?><container version=\"1.0\" xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\">"
            + "<rootfiles><rootfile full-path=\"OEBPS/content.opf\" media-type=\"application/oebps-package+xml\"/></rootfiles></container>");
        Add("OEBPS/content.opf",
            "<?xml version=\"1.0\" encoding=\"utf-8\"?><package xmlns=\"http://www.idpf.org/2007/opf\" version=\"2.0\">"
            + "<metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:opf=\"http://www.idpf.org/2007/opf\">"
            + metadata + "</metadata><manifest>" + manifest + "</manifest></package>");
        Add("OEBPS/text.xhtml", "<html><body><p>Text only.</p></body></html>");

        foreach (var (name, bytes) in files ?? [])
        {
            using var stream = zip.CreateEntry(name).Open();
            stream.Write(bytes);
        }

        return path;
    }

    [Fact]
    public void TitleIsDecoded_CreatorRead_UuidIsNotAnIsbn()
    {
        var path = MakeEpub(_dir, "q.epub",
            "<dc:title>La questione agraria e l&amp;#x27;emigrazione in Calabria (texto OCR, HathiTrust)</dc:title>"
            + "<dc:creator>Taruffi, Dino</dc:creator>"
            + "<dc:identifier>urn:uuid:b0e62c59-1111-2222-3333-444455556666</dc:identifier>");

        var hints = EpubOpf.Read(path);

        Assert.NotNull(hints);
        Assert.Equal("La questione agraria e l'emigrazione in Calabria (texto OCR, HathiTrust)", hints!.Title);
        Assert.Equal("Taruffi, Dino", hints.Author);
        Assert.Empty(hints.Isbns);
    }

    [Theory]
    [InlineData("<dc:identifier opf:scheme=\"ISBN\">978-84-9781-366-2</dc:identifier>")]
    [InlineData("<dc:identifier>urn:isbn:9788497813662</dc:identifier>")]
    [InlineData("<dc:identifier>9788497813662</dc:identifier>")]
    public void IsbnInAnyForm_IsRead(string identifier)
    {
        var path = MakeEpub(_dir, $"{Guid.NewGuid():N}.epub", "<dc:title>El arsenal de Cartagena en el siglo XVIII</dc:title>" + identifier);

        Assert.Equal(["9788497813662"], EpubOpf.Read(path)!.Isbns);
    }

    [Fact]
    public void CoverMeta_PointsAtTheCoverImage()
    {
        var path = MakeEpub(_dir, "c.epub",
            "<dc:title>Quiet Harbour</dc:title><meta name=\"cover\" content=\"img1\"/>",
            "<item id=\"img1\" href=\"../Images/pic%201.jpg\" media-type=\"image/jpeg\"/>");

        Assert.Equal("Images/pic 1.jpg", EpubOpf.Read(path)!.CoverPath);
    }

    [Fact]
    public void NotAnEpub_GivesNothing()
    {
        var path = Path.Combine(_dir, "broken.epub");
        File.WriteAllBytes(path, new byte[100]);

        Assert.Null(EpubOpf.Read(path));
    }
}
