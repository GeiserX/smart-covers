using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SmartCovers;

/// <summary>
/// What an Anna's Archive file name says about its book.
/// </summary>
/// <param name="Title">The title field, cleaned.</param>
/// <param name="Author">The author field, cleaned, or null when the name has none.</param>
/// <param name="Isbn">A checksum-valid ISBN from an "isbn13"/"isbn10" field, or null.</param>
/// <param name="Oclc">An OCLC number from an "oclc" field, or null.</param>
internal sealed record AnnasArchiveName(string Title, string? Author, string? Isbn, string? Oclc);

/// <summary>
/// Pure helpers for reading book identity out of names: Anna's Archive file names,
/// ISBNs, and author lists. No I/O.
/// </summary>
internal static class BookNameParser
{
    private const string FieldSeparator = " -- ";

    private static readonly Regex Md5Field = new(@"^[0-9a-f]{32}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AnnasArchiveField = new(@"^Anna[’'`]?s Archive$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Isbn13Field = new(@"^isbn13\s+(97[89]\d{10})$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Isbn10Field = new(@"^isbn10\s+(\d{9}[\dXx])$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex OclcField = new(@"^oclc\s+(\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // "Spain, 1989": the place and year of publication. Never the first field: a
    // title can read the same ("Berlín, 1945").
    private static readonly Regex PlaceYearField = new(@"^[^,]+,\s*\d{4}$", RegexOptions.Compiled);

    // A volume after a dash belongs to the title: "Los Miserables - Tomo I".
    private static readonly Regex VolumeTail = new(
        @"^(?:tomo|tome|vol\.?|volume|volumen|libro|livre|parte|part|book|band)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RomanNumeral = new(@"^[IVXLCDM]+$", RegexOptions.Compiled);

    // The separators of a list of people: "A, B, and C", "A & B", "A y B".
    private static readonly Regex NameListSeparator = new(
        @"\s*[,;]\s*(?:(?:and|y|&)\s+)?|\s+(?:and|y|&)\s+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Lower-case words a person's name may carry: "Juan de Goyeneche", "Ludwig van Beethoven".
    private static readonly HashSet<string> NameParticles = new(StringComparer.Ordinal)
    {
        "de", "del", "la", "las", "los", "y", "da", "das", "do", "dos", "di", "van", "von", "der", "den", "le", "du", "of"
    };

    /// <summary>
    /// The tag a text-only rebuild carries: "(texto OCR, HathiTrust)", "(text OCR)".
    /// </summary>
    internal static readonly Regex OcrTag = new(@"\s*\((?:texto|text)\s+OCR[^)]*\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Life dates on an author: "Goyeneche, Juan de, 1656-1735".
    private static readonly Regex LifeDates = new(@",?\s*\d{4}\s*-\s*(?:\d{4})?\s*$", RegexOptions.Compiled);

    /// <summary>
    /// Reads a name in the Anna's Archive download style:
    /// "Title -- Author -- Place, Year -- Publisher -- isbn13 978... -- md5 -- Anna’s Archive".
    /// </summary>
    /// <returns>False when the name is not in that style; it is then left to the ordinary parser.</returns>
    internal static bool TryParseAnnasArchive(string? name, out AnnasArchiveName? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(name) || !name.Contains(FieldSeparator, StringComparison.Ordinal))
        {
            return false;
        }

        var fields = name.Split(FieldSeparator, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 2)
        {
            return false;
        }

        // Only a name that carries Anna's own markers is read this way. A title that
        // just happens to contain " -- " is left alone.
        var marked = AnnasArchiveField.IsMatch(fields[^1]) || fields.Any(f => Md5Field.IsMatch(f));
        if (!marked)
        {
            return false;
        }

        string? isbn = null;
        string? oclc = null;
        var kept = new List<string>();

        for (var index = 0; index < fields.Length; index++)
        {
            var field = fields[index];
            if (Md5Field.IsMatch(field) || AnnasArchiveField.IsMatch(field) || (index > 0 && PlaceYearField.IsMatch(field)))
            {
                continue;
            }

            var isbnMatch = Isbn13Field.Match(field);
            if (!isbnMatch.Success)
            {
                isbnMatch = Isbn10Field.Match(field);
            }

            if (isbnMatch.Success)
            {
                isbn ??= NormalizeIsbn(isbnMatch.Groups[1].Value);
                continue;
            }

            var oclcMatch = OclcField.Match(field);
            if (oclcMatch.Success)
            {
                oclc ??= oclcMatch.Groups[1].Value;
                continue;
            }

            kept.Add(field);
        }

        if (kept.Count == 0)
        {
            return false;
        }

        var author = kept.Count > 1 ? CleanAnnasAuthor(kept[1]) : null;
        var title = CleanAnnasTitle(kept[0], author);

        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        result = new AnnasArchiveName(title, string.IsNullOrWhiteSpace(author) ? null : author, isbn, oclc);
        return true;
    }

    private static string CleanAnnasTitle(string title, string? author)
    {
        title = OcrTag.Replace(title, string.Empty);

        // Anna's Archive writes "_" where the original title had ":" or "/".
        title = title.Replace(" _ ", ": ", StringComparison.Ordinal);

        // An author that leaked into the title field after " - ":
        // "Archivística en Navarra - [Juan José Martinena Ruiz ___ et",
        // "El arsenal de Cartagena en el siglo XVIII - María Teresa". Only a tail
        // that opens a bracket or repeats a name of the author field is cut; a
        // subtitle ("Star Wars - Una Nueva Esperanza") or a volume ("... - Tomo I")
        // stays.
        var dash = title.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 2
            && OnlineCoverFetcher.IsSearchableTitle(title[..dash])
            && LooksLikeLeakedName(title[(dash + 3)..], author))
        {
            title = title[..dash];
        }

        return title.Trim().TrimEnd('_', ',', ';', ':', ' ').Trim();
    }

    private static bool LooksLikeLeakedName(string tail, string? author)
    {
        tail = tail.Trim();
        if (tail.StartsWith('['))
        {
            return true;
        }

        var words = tail.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length is < 1 or > 4
            || tail.Any(char.IsDigit)
            || !words.All(w => char.IsUpper(w[0]))
            || VolumeTail.IsMatch(tail)
            || RomanNumeral.IsMatch(words[^1]))
        {
            return false;
        }

        return author != null && NameTokens(tail).Overlaps(NameTokens(author));
    }

    private static string CleanAnnasAuthor(string author)
    {
        author = author.Trim().TrimEnd('_').Trim();
        author = LifeDates.Replace(author, string.Empty);
        return author.Trim().TrimEnd('_', ',', ' ').Trim();
    }

    /// <summary>
    /// The ISBN with hyphens and spaces removed, or null when it fails its checksum.
    /// </summary>
    internal static string? NormalizeIsbn(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var sb = new StringBuilder(13);
        foreach (var c in raw)
        {
            if (char.IsAsciiDigit(c) || c is 'X' or 'x')
            {
                sb.Append(char.ToUpperInvariant(c));
            }
            else if (c is not ('-' or ' ' or '‐' or '‑'))
            {
                return null;
            }
        }

        var isbn = sb.ToString();
        return isbn.Length switch
        {
            13 when IsValidIsbn13(isbn) => isbn,
            10 when IsValidIsbn10(isbn) => isbn,
            _ => null
        };
    }

    private static bool IsValidIsbn13(string isbn)
    {
        if (!isbn.All(char.IsAsciiDigit) || !(isbn.StartsWith("978", StringComparison.Ordinal) || isbn.StartsWith("979", StringComparison.Ordinal)))
        {
            return false;
        }

        var sum = 0;
        for (var i = 0; i < 12; i++)
        {
            sum += (isbn[i] - '0') * (i % 2 == 0 ? 1 : 3);
        }

        return (10 - (sum % 10)) % 10 == isbn[12] - '0';
    }

    private static bool IsValidIsbn10(string isbn)
    {
        var sum = 0;
        for (var i = 0; i < 10; i++)
        {
            int value;
            if (char.IsAsciiDigit(isbn[i]))
            {
                value = isbn[i] - '0';
            }
            else if (isbn[i] == 'X' && i == 9)
            {
                value = 10;
            }
            else
            {
                return false;
            }

            sum += value * (10 - i);
        }

        return sum % 11 == 0;
    }

    /// <summary>
    /// The first name of an author list: "A, B, and C", "A &amp; B", "A; B" all give "A".
    /// A single "Surname, Given" stays whole: only a list with a conjunction or with
    /// two or more commas is cut at a comma.
    /// </summary>
    internal static string? FirstAuthor(string? author)
    {
        if (string.IsNullOrWhiteSpace(author))
        {
            return null;
        }

        var first = author;
        var wasList = false;
        foreach (var separator in new[] { ";", " & ", " and ", " / " })
        {
            var idx = first.IndexOf(separator, StringComparison.OrdinalIgnoreCase);
            if (idx > 0)
            {
                first = first[..idx];
                wasList = true;
            }
        }

        if (first.Count(c => c == ',') >= 2 || (wasList && first.Contains(',', StringComparison.Ordinal)))
        {
            first = first[..first.IndexOf(',', StringComparison.Ordinal)];
        }

        first = first.Trim().TrimEnd(',', ' ');
        return first.Length == 0 ? null : first;
    }

    /// <summary>
    /// True when the text reads as a list of people: "Douglas Stone, Bruce Patton,
    /// and Sheila Heen", "Eduardo Matos Moctezuma &amp; Luis Millones". Every part must
    /// read as a full name (two to four capitalised words), so a title with a
    /// comma or an ampersand ("Pride &amp; Prejudice", "Veni, vidi, vici") is not one.
    /// </summary>
    internal static bool LooksLikeAuthorList(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = NameListSeparator.Split(text.Trim())
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();

        return parts.Count >= 2 && parts.All(LooksLikePersonName);
    }

    private static bool LooksLikePersonName(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length is >= 2 and <= 4
            && !text.Any(char.IsDigit)
            && char.IsUpper(words[0][0])
            && char.IsUpper(words[^1][0])
            && words.All(w => char.IsUpper(w[0]) || NameParticles.Contains(w));
    }

    /// <summary>
    /// The author an audio tag names. Tags often hold "SURNAME, Given" ("ASIMOV,
    /// Isaac"), which reads as "Isaac Asimov"; a comma between two full names
    /// ("José Manuel García-Margallo, Fernando Eguidazu") is a list, so the first
    /// name is kept.
    /// </summary>
    internal static string? AuthorFromTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        tag = tag.Trim();
        var parts = tag.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && !parts[0].Contains(' ', StringComparison.Ordinal) && WordCount(parts[1]) <= 3)
        {
            return $"{parts[1]} {FixCapitals(parts[0])}";
        }

        return FirstAuthor(parts.Length >= 2 ? parts[0] : tag);
    }

    private static string FixCapitals(string word)
        => word.Length > 1 && word.All(c => !char.IsLetter(c) || char.IsUpper(c))
            ? CultureInfo.InvariantCulture.TextInfo.ToTitleCase(word.ToLowerInvariant())
            : word;

    /// <summary>
    /// The number of space-separated words.
    /// </summary>
    internal static int WordCount(string? text)
        => string.IsNullOrWhiteSpace(text) ? 0 : text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>
    /// Lower case with accents removed, for comparing names loosely.
    /// </summary>
    internal static string Fold(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>
    /// The name words of an author, folded, three letters or longer ("Isaac", "Asimov").
    /// </summary>
    internal static HashSet<string> NameTokens(string? author)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(author))
        {
            return tokens;
        }

        var sb = new StringBuilder();
        foreach (var c in Fold(author) + " ")
        {
            if (char.IsLetter(c))
            {
                sb.Append(c);
                continue;
            }

            if (sb.Length >= 3)
            {
                tokens.Add(sb.ToString());
            }

            sb.Clear();
        }

        return tokens;
    }

    /// <summary>
    /// True when any of the catalogue's authors shares a name word with ours.
    /// </summary>
    internal static bool SharesAuthorName(IEnumerable<string> catalogueAuthors, string author)
    {
        var ours = NameTokens(author);
        return ours.Count > 0 && catalogueAuthors.Any(a => NameTokens(a).Overlaps(ours));
    }
}
