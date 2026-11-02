using System.Text;
using SkiaSharp;

namespace SmartCovers;

/// <summary>
/// Draws a plain title-and-author cover for a book no catalogue has art for.
///
/// Jellyfin's plugin scanner resolves the type of every field in this assembly,
/// compiler-generated ones included, before any plugin code runs. So no SkiaSharp
/// type may ever sit in a field here: every Skia object is a local inside a
/// synchronous method, with no lambdas, local functions or async code around it,
/// and the palette is kept as plain ARGB numbers.
/// </summary>
internal static class GeneratedCover
{
    /// <summary>Cover width in pixels (2:3 like a book).</summary>
    internal const int Width = 600;

    /// <summary>Cover height in pixels.</summary>
    internal const int Height = 900;

    /// <summary>The largest title size, in pixels.</summary>
    internal const float MaxTitleSize = 64f;

    /// <summary>The smallest title size; below this the title is cut with an ellipsis.</summary>
    internal const float MinTitleSize = 28f;

    /// <summary>Most lines the title may take.</summary>
    internal const int MaxTitleLines = 6;

    private const float AuthorSize = 30f;
    private const int MaxAuthorLines = 2;
    private const float Margin = 60f;

    // Written straight after the JPEG's start-of-image marker as a COM segment, so a
    // generated cover can be told apart from a real one later and replaced.
    private static readonly byte[] Marker = Encoding.ASCII.GetBytes("SmartCovers:generated:v1");

    private static readonly uint[] Palette =
    [
        0xFF2E4057, 0xFF4F3B57, 0xFF1F5F5B, 0xFF6B3E26,
        0xFF3B4F2E, 0xFF5A2A3A, 0xFF2B3A67, 0xFF574B2E
    ];

    // Used when the system font lookup finds nothing that can draw the text
    // (a host without fontconfig). Never bundled: these are the host's own files.
    private static readonly string[] RegularFontFiles =
    [
        "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
        "/usr/share/fonts/dejavu/DejaVuSans.ttf",
        "/usr/share/fonts/TTF/DejaVuSans.ttf",
        "/usr/share/fonts/noto/NotoSans-Regular.ttf",
        "/usr/share/fonts/truetype/noto/NotoSans-Regular.ttf",
        "/System/Library/Fonts/Supplemental/Arial.ttf",
        "C:\\Windows\\Fonts\\arial.ttf"
    ];

    private static readonly string[] BoldFontFiles =
    [
        "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf",
        "/usr/share/fonts/dejavu/DejaVuSans-Bold.ttf",
        "/usr/share/fonts/TTF/DejaVuSans-Bold.ttf",
        "/usr/share/fonts/noto/NotoSans-Bold.ttf",
        "/usr/share/fonts/truetype/noto/NotoSans-Bold.ttf",
        "/System/Library/Fonts/Supplemental/Arial Bold.ttf",
        "C:\\Windows\\Fonts\\arialbd.ttf"
    ];

    /// <summary>
    /// The background colour slot for a book, stable across runs (FNV-1a over UTF-8).
    /// </summary>
    internal static int PaletteIndex(string title, string? author)
    {
        const uint offset = 2166136261;
        const uint prime = 16777619;
        var hash = offset;
        foreach (var b in Encoding.UTF8.GetBytes(title + "\n" + (author ?? string.Empty)))
        {
            hash ^= b;
            hash *= prime;
        }

        return (int)(hash % (uint)Palette.Length);
    }

    /// <summary>
    /// True when the bytes start with a JPEG carrying the generated-cover marker.
    /// </summary>
    internal static bool IsGenerated(ReadOnlySpan<byte> head)
    {
        if (head.Length < 6 + Marker.Length || head[0] != 0xFF || head[1] != 0xD8 || head[2] != 0xFF || head[3] != 0xFE)
        {
            return false;
        }

        return head.Slice(6, Marker.Length).SequenceEqual(Marker);
    }

    /// <summary>
    /// True when the image file at <paramref name="path"/> is a generated cover.
    /// </summary>
    internal static bool IsGeneratedFile(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> head = stackalloc byte[64];
            var read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            return IsGenerated(head[..read]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Renders the cover as a marked JPEG, or returns null when no font on this host
    /// can draw the text.
    /// </summary>
    /// <param name="title">The book title.</param>
    /// <param name="author">The author, or null.</param>
    /// <param name="jpegQuality">JPEG quality, 1 to 100.</param>
    /// <param name="fontPath">A font file to use for every line instead of the system fonts (tests).</param>
    internal static byte[]? Render(string title, string? author, int jpegQuality, string? fontPath = null)
    {
        title = title.Trim();
        author = string.IsNullOrWhiteSpace(author) ? null : author.Trim();
        if (title.Length == 0)
        {
            return null;
        }

        using var titleFace = PickTypeface(title, bold: true, fontPath);
        if (titleFace == null)
        {
            return null;
        }

        using var authorFace = author == null ? null : PickTypeface(author, bold: false, fontPath);

        var info = new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        if (surface == null)
        {
            return null;
        }

        var canvas = surface.Canvas;
        canvas.Clear(new SKColor(Palette[PaletteIndex(title, author)]));

        using (var rule = new SKPaint { Color = new SKColor(0x66FFFFFF), IsStroke = true, StrokeWidth = 3, IsAntialias = true })
        {
            canvas.DrawRect(new SKRect(24, 24, Width - 24, Height - 24), rule);
        }

        using var ink = new SKPaint { Color = SKColors.White, IsAntialias = true };
        var maxWidth = Width - (2 * Margin);

        // Title: the largest size at which it fits, else the minimum size cut short.
        using (var titleFont = new SKFont(titleFace, MaxTitleSize))
        {
            var lines = LayoutTitle(title, titleFont, maxWidth);
            var lineHeight = titleFont.Size * 1.2f;
            var top = 80f + ((520f - (lines.Count * lineHeight)) / 2f);
            for (var i = 0; i < lines.Count; i++)
            {
                canvas.DrawText(lines[i], Width / 2f, top + ((i + 1) * lineHeight) - (lineHeight * 0.25f), SKTextAlign.Center, titleFont, ink);
            }
        }

        if (author != null && authorFace != null)
        {
            using var authorFont = new SKFont(authorFace, AuthorSize);
            var lines = Wrap(author, authorFont, maxWidth, MaxAuthorLines, out _);
            var lineHeight = AuthorSize * 1.25f;
            var top = Height - 110f - (lines.Count * lineHeight);
            for (var i = 0; i < lines.Count; i++)
            {
                canvas.DrawText(lines[i], Width / 2f, top + ((i + 1) * lineHeight), SKTextAlign.Center, authorFont, ink);
            }
        }

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, Math.Clamp(jpegQuality, 1, 100));
        if (data == null)
        {
            return null;
        }

        return AddMarker(data.ToArray());
    }

    /// <summary>
    /// The title's lines at the size it ends up drawn at. Shrinks the font from
    /// <see cref="MaxTitleSize"/> until the title fits in <see cref="MaxTitleLines"/>;
    /// at <see cref="MinTitleSize"/> whatever does not fit is cut with an ellipsis.
    /// The font's size is left at the chosen size.
    /// </summary>
    internal static List<string> LayoutTitle(string title, SKFont font, float maxWidth)
    {
        for (var size = MaxTitleSize; size >= MinTitleSize; size -= 4f)
        {
            font.Size = size;
            var lines = Wrap(title, font, maxWidth, MaxTitleLines, out var overflow);
            if (!overflow)
            {
                return lines;
            }
        }

        font.Size = MinTitleSize;
        return Wrap(title, font, maxWidth, MaxTitleLines, out _);
    }

    /// <summary>
    /// Greedy word wrap. A word too wide for a line on its own is cut with an
    /// ellipsis; so is the last line when the text does not fit.
    /// </summary>
    internal static List<string> Wrap(string text, SKFont font, float maxWidth, int maxLines, out bool overflow)
    {
        overflow = false;
        var lines = new List<string>();
        var current = new StringBuilder();

        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = current.Length == 0 ? word : current + " " + word;
            if (font.MeasureText(candidate) <= maxWidth)
            {
                current.Clear().Append(candidate);
                continue;
            }

            if (current.Length > 0)
            {
                lines.Add(current.ToString());
                current.Clear();
            }

            if (font.MeasureText(word) > maxWidth)
            {
                overflow = true;
                current.Append(Ellipsize(word, font, maxWidth));
            }
            else
            {
                current.Append(word);
            }
        }

        if (current.Length > 0)
        {
            lines.Add(current.ToString());
        }

        if (lines.Count > maxLines)
        {
            overflow = true;
            lines.RemoveRange(maxLines, lines.Count - maxLines);
            lines[^1] = Ellipsize(lines[^1] + "…", font, maxWidth);
        }

        return lines;
    }

    private static string Ellipsize(string text, SKFont font, float maxWidth)
    {
        if (font.MeasureText(text) <= maxWidth)
        {
            return text;
        }

        var cut = text.TrimEnd('…');
        while (cut.Length > 1 && font.MeasureText(cut + "…") > maxWidth)
        {
            cut = cut[..^1];
        }

        return cut.TrimEnd() + "…";
    }

    private static SKTypeface? PickTypeface(string text, bool bold, string? fontPath)
    {
        if (fontPath != null)
        {
            return OpenIfCovers(fontPath, text);
        }

        var style = bold ? SKFontStyle.Bold : SKFontStyle.Normal;
        var byFamily = SKTypeface.FromFamilyName("sans-serif", style);
        if (Covers(byFamily, text))
        {
            return byFamily;
        }

        byFamily?.Dispose();

        // A script the default face lacks (CJK, Greek...): ask for a face that has the
        // first missing character, and keep it if it covers the whole text.
        var missing = FirstMissingCodepoint(text);
        if (missing > 0)
        {
            var matched = SKFontManager.Default.MatchCharacter("sans-serif", style, null, missing);
            if (Covers(matched, text))
            {
                return matched;
            }

            matched?.Dispose();
        }

        foreach (var file in bold ? BoldFontFiles : RegularFontFiles)
        {
            if (!File.Exists(file))
            {
                continue;
            }

            var face = OpenIfCovers(file, text);
            if (face != null)
            {
                return face;
            }
        }

        return null;
    }

    private static SKTypeface? OpenIfCovers(string file, string text)
    {
        var face = File.Exists(file) ? SKTypeface.FromFile(file) : null;
        if (Covers(face, text))
        {
            return face;
        }

        face?.Dispose();
        return null;
    }

    private static bool Covers(SKTypeface? face, string text)
    {
        if (face == null || face.GlyphCount == 0)
        {
            return false;
        }

        var visible = VisibleText(text);
        if (visible.Length == 0)
        {
            return true;
        }

        using var font = new SKFont(face);
        return font.ContainsGlyphs(visible);
    }

    private static int FirstMissingCodepoint(string text)
    {
        var visible = VisibleText(text);
        using var face = SKTypeface.FromFamilyName("sans-serif");
        if (face == null)
        {
            return visible.Length > 0 ? char.ConvertToUtf32(visible, 0) : 0;
        }

        using var font = new SKFont(face);
        for (var i = 0; i < visible.Length; i += char.IsSurrogatePair(visible, i) ? 2 : 1)
        {
            var codepoint = char.ConvertToUtf32(visible, i);
            if (!font.ContainsGlyph(codepoint))
            {
                return codepoint;
            }
        }

        return 0;
    }

    private static string VisibleText(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (!char.IsWhiteSpace(c) && !char.IsControl(c))
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    private static byte[] AddMarker(byte[] jpeg)
    {
        // FF FE <length, big endian, counting itself> <marker>, right after FF D8.
        var segmentLength = Marker.Length + 2;
        var result = new byte[jpeg.Length + 2 + segmentLength];
        result[0] = jpeg[0];
        result[1] = jpeg[1];
        result[2] = 0xFF;
        result[3] = 0xFE;
        result[4] = (byte)(segmentLength >> 8);
        result[5] = (byte)(segmentLength & 0xFF);
        Marker.CopyTo(result, 6);
        Array.Copy(jpeg, 2, result, 6 + Marker.Length, jpeg.Length - 2);
        return result;
    }
}
