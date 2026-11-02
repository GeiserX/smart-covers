using System.IO.Compression;
using System.Net;
using System.Xml;
using System.Xml.Linq;

namespace SmartCovers;

/// <summary>
/// What an EPUB's own package document (the OPF) says about the book.
/// </summary>
/// <param name="Title">dc:title, entity-decoded, or null.</param>
/// <param name="Author">The first dc:creator, or null.</param>
/// <param name="Isbns">Every checksum-valid ISBN among the dc:identifier values.</param>
/// <param name="CoverPath">The archive path of the image the OPF marks as the cover, or null.</param>
internal sealed record EpubHints(string? Title, string? Author, IReadOnlyList<string> Isbns, string? CoverPath);

/// <summary>
/// Reads an EPUB's package document. File names are often cut short or carry
/// download-site noise; the OPF holds the real title, author and ISBN.
/// </summary>
internal static class EpubOpf
{
    private const int MaxXmlBytes = 4 * 1024 * 1024;

    /// <summary>
    /// Reads the OPF of the EPUB at <paramref name="path"/>, or returns null when it cannot.
    /// </summary>
    internal static EpubHints? Read(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            return Read(zip);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the OPF from an open EPUB archive, or returns null when it has none.
    /// </summary>
    internal static EpubHints? Read(ZipArchive zip)
    {
        try
        {
            var opfPath = FindOpfPath(zip);
            var opfEntry = opfPath == null ? null : zip.GetEntry(opfPath);
            opfEntry ??= zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".opf", StringComparison.OrdinalIgnoreCase));
            if (opfEntry == null)
            {
                return null;
            }

            var opf = LoadXml(opfEntry);
            if (opf?.Root == null)
            {
                return null;
            }

            var title = Text(opf.Descendants().FirstOrDefault(e => e.Name.LocalName == "title"));
            var author = Text(opf.Descendants().FirstOrDefault(e => e.Name.LocalName == "creator"));

            var isbns = opf.Descendants()
                .Where(e => e.Name.LocalName == "identifier")
                .Select(IsbnOf)
                .OfType<string>()
                .Distinct(StringComparer.Ordinal)
                .ToList();

            return new EpubHints(title, author, isbns, CoverPathOf(opf, opfEntry.FullName));
        }
        catch (Exception ex) when (ex is XmlException or IOException or InvalidDataException)
        {
            return null;
        }
    }

    private static string? FindOpfPath(ZipArchive zip)
    {
        var container = zip.GetEntry("META-INF/container.xml");
        if (container == null)
        {
            return null;
        }

        var doc = LoadXml(container);
        return doc?.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "rootfile")
            ?.Attribute("full-path")?.Value;
    }

    private static XDocument? LoadXml(ZipArchiveEntry entry)
    {
        if (entry.Length > MaxXmlBytes)
        {
            return null;
        }

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            MaxCharactersInDocument = MaxXmlBytes
        };

        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, settings);
        return XDocument.Load(reader);
    }

    private static string? Text(XElement? element)
    {
        if (element == null)
        {
            return null;
        }

        // Some tools escape twice ("l&amp;#x27;emigrazione"); one more decode is safe.
        var value = WebUtility.HtmlDecode(element.Value).Trim();
        return value.Length == 0 ? null : value;
    }

    private static string? IsbnOf(XElement identifier)
    {
        var value = identifier.Value.Trim();
        var scheme = identifier.Attributes().FirstOrDefault(a => a.Name.LocalName == "scheme")?.Value;

        foreach (var prefix in new[] { "urn:isbn:", "isbn:" })
        {
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return BookNameParser.NormalizeIsbn(value[prefix.Length..]);
            }
        }

        if (string.Equals(scheme, "ISBN", StringComparison.OrdinalIgnoreCase))
        {
            return BookNameParser.NormalizeIsbn(value);
        }

        // A bare 13-digit or 10-digit value that passes the checksum is an ISBN too;
        // a uuid or a URL never passes.
        return value.Length <= 17 ? BookNameParser.NormalizeIsbn(value) : null;
    }

    private static string? CoverPathOf(XDocument opf, string opfPath)
    {
        var items = opf.Descendants().Where(e => e.Name.LocalName == "item").ToList();

        // EPUB 3: <item properties="cover-image">.
        var cover = items.FirstOrDefault(i => (i.Attribute("properties")?.Value ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Contains("cover-image", StringComparer.Ordinal));

        // EPUB 2: <meta name="cover" content="item-id">.
        if (cover == null)
        {
            var coverId = opf.Descendants()
                .Where(e => e.Name.LocalName == "meta")
                .FirstOrDefault(m => string.Equals(m.Attribute("name")?.Value, "cover", StringComparison.OrdinalIgnoreCase))
                ?.Attribute("content")?.Value;

            if (coverId != null)
            {
                cover = items.FirstOrDefault(i => string.Equals(i.Attribute("id")?.Value, coverId, StringComparison.Ordinal));
            }
        }

        var href = cover?.Attribute("href")?.Value;
        if (string.IsNullOrEmpty(href))
        {
            return null;
        }

        var opfDir = opfPath.Contains('/', StringComparison.Ordinal) ? opfPath[..opfPath.LastIndexOf('/')] : string.Empty;
        var combined = opfDir.Length == 0 ? Uri.UnescapeDataString(href) : $"{opfDir}/{Uri.UnescapeDataString(href)}";
        return NormalizeArchivePath(combined);
    }

    // Resolves "OEBPS/Text/../Images/cover.jpg" to "OEBPS/Images/cover.jpg".
    private static string NormalizeArchivePath(string path)
    {
        var parts = new List<string>();
        foreach (var part in path.Split('/'))
        {
            if (part == "..")
            {
                if (parts.Count > 0)
                {
                    parts.RemoveAt(parts.Count - 1);
                }
            }
            else if (part.Length > 0 && part != ".")
            {
                parts.Add(part);
            }
        }

        return string.Join('/', parts);
    }
}
