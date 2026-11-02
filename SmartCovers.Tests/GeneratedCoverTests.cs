using System.Reflection;
using SkiaSharp;
using Xunit;

namespace SmartCovers.Tests;

/// <summary>
/// Finds a font file to draw with. The CI and test SkiaSharp native has no
/// fontconfig, so the system lookup finds nothing there; the tests hand a file in.
/// </summary>
internal static class TestFonts
{
    private static readonly string[] Candidates =
    [
        "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
        "/usr/share/fonts/dejavu/DejaVuSans.ttf",
        "/usr/share/fonts/TTF/DejaVuSans.ttf",
        "/System/Library/Fonts/Supplemental/Arial.ttf",
        "C:\\Windows\\Fonts\\arial.ttf"
    ];

    public static string? Path => Candidates.FirstOrDefault(File.Exists);
}

public class GeneratedCoverTests
{
    [Fact]
    public void OnCi_AFontIsAvailable_SoTheDrawingTestsRun()
    {
        // The drawing tests return early without a font; on CI that would hide them.
        if (Environment.GetEnvironmentVariable("CI") == "true" && OperatingSystem.IsLinux())
        {
            Assert.NotNull(TestFonts.Path);
        }
    }

    [Fact]
    public void Render_GivesAMarked600x900Jpeg()
    {
        if (TestFonts.Path is not { } font)
        {
            return;
        }

        var bytes = GeneratedCover.Render("Bel-Abbès et son arrondissement", "Bastide, L.", 85, font);

        Assert.NotNull(bytes);
        Assert.Equal([0xFF, 0xD8, 0xFF, 0xFE], bytes![..4]);
        Assert.True(GeneratedCover.IsGenerated(bytes));
        Assert.Equal(ImageFormatOf(bytes), MediaBrowser.Model.Drawing.ImageFormat.Jpg);

        using var bitmap = SKBitmap.Decode(bytes);
        Assert.Equal(600, bitmap.Width);
        Assert.Equal(900, bitmap.Height);
    }

    private static MediaBrowser.Model.Drawing.ImageFormat? ImageFormatOf(byte[] bytes)
        => CoverImageProvider.DetectImageFormat(bytes).Format;

    [Fact]
    public void IsGenerated_IsFalseForARealJpeg()
    {
        var real = new byte[5000];
        real[0] = 0xFF; real[1] = 0xD8; real[2] = 0xFF; real[3] = 0xE0;

        Assert.False(GeneratedCover.IsGenerated(real));
        Assert.False(GeneratedCover.IsGenerated(Array.Empty<byte>()));
    }

    [Fact]
    public void Render_IsDeterministic_AndTheColourDependsOnTheBook()
    {
        if (TestFonts.Path is not { } font)
        {
            return;
        }

        var a = GeneratedCover.Render("Dizionario dei luoghi della Calabria", "Valente, Gustavo", 85, font);
        var b = GeneratedCover.Render("Dizionario dei luoghi della Calabria", "Valente, Gustavo", 85, font);
        Assert.Equal(a, b);

        Assert.NotEqual(
            GeneratedCover.PaletteIndex("Dizionario dei luoghi della Calabria", "Valente, Gustavo"),
            GeneratedCover.PaletteIndex("Ragguaglio di Cosenza e di trent'una sue nobili famiglie", "Sambiasi, Girolamo"));
    }

    [Fact]
    public void LongTitle_WrapsAndShrinks_WithoutLeavingTheCover()
    {
        if (TestFonts.Path is not { } font)
        {
            return;
        }

        using var face = SKTypeface.FromFile(font);
        using var skFont = new SKFont(face, GeneratedCover.MaxTitleSize);
        const float maxWidth = 480f;

        var lines = GeneratedCover.LayoutTitle(
            "La Vasconie. Étude historique et critique sur les origines du royaume de Navarre", skFont, maxWidth);

        Assert.InRange(lines.Count, 2, GeneratedCover.MaxTitleLines);
        Assert.True(skFont.Size >= GeneratedCover.MinTitleSize);
        Assert.All(lines, line => Assert.True(skFont.MeasureText(line) <= maxWidth, line));
    }

    [Fact]
    public void TooLongTitle_IsCutWithAnEllipsis()
    {
        if (TestFonts.Path is not { } font)
        {
            return;
        }

        using var face = SKTypeface.FromFile(font);
        using var skFont = new SKFont(face, GeneratedCover.MaxTitleSize);
        var title = string.Join(' ', Enumerable.Repeat("Navarra", 80));

        var lines = GeneratedCover.LayoutTitle(title, skFont, 480f);

        Assert.Equal(GeneratedCover.MaxTitleLines, lines.Count);
        Assert.EndsWith("…", lines[^1], StringComparison.Ordinal);
        Assert.Equal(GeneratedCover.MinTitleSize, skFont.Size);
    }

    [Theory]
    [InlineData("Entre el deshonor y la miseria. Infancia abandonada en Guipúzcoa y Navarra, siglos XVIII y XIX", "Valverde, Lola")]
    [InlineData("La Domination espagnole en Algérie et au Maroc", "Froelicher, Émile")]
    public void AccentedText_IsDrawn(string title, string author)
    {
        if (TestFonts.Path is not { } font)
        {
            return;
        }

        Assert.NotNull(GeneratedCover.Render(title, author, 85, font));
    }

    [Fact]
    public void NoFont_GivesNothing()
    {
        Assert.Null(GeneratedCover.Render("Quiet Harbour", "Ana Ruiz", 85, "/nonexistent/font.ttf"));
    }

    [Fact]
    public void NoFieldInThePlugin_HasASkiaSharpType()
    {
        // Jellyfin's plugin scan resolves every field type, compiler-generated ones
        // included, before any plugin code runs. A SkiaSharp type in a field is the
        // NotSupported trap; this gate fails the build before that ships.
        var skia = typeof(SKBitmap).Assembly;
        var offenders = new List<string>();

        foreach (var type in typeof(CoverImageProvider).Assembly.GetTypes())
        {
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (Mentions(field.FieldType, skia))
                {
                    offenders.Add($"{type.FullName}.{field.Name}: {field.FieldType}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    private static bool Mentions(Type type, Assembly assembly)
    {
        if (type.Assembly == assembly)
        {
            return true;
        }

        if (type.HasElementType && Mentions(type.GetElementType()!, assembly))
        {
            return true;
        }

        return type.IsGenericType && type.GetGenericArguments().Any(t => Mentions(t, assembly));
    }
}
