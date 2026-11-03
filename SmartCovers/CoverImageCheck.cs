using SkiaSharp;

namespace SmartCovers;

/// <summary>
/// Looks at a downloaded cover and says why it cannot be one: a shape no book
/// cover has, or Google Books' "image not available" picture.
///
/// Every Skia object is a local in a synchronous static method, for the reason
/// given on <see cref="GeneratedCover"/>: Jellyfin's plugin scanner must never
/// meet a Skia type in a field.
/// </summary>
internal static class CoverImageCheck
{
    /// <summary>
    /// The widest a cover may be, as width over height. Book and audiobook covers
    /// run from about 0.6 (tall paperbacks) to 1.0 (square audiobooks); oblong
    /// picture books reach about 1.4. Google Books at zoom=0 returns the top strip
    /// of a scanned title page for some volumes, 575x92, a ratio of 6.25.
    /// </summary>
    internal const double MaxWidthOverHeight = 1.5;

    /// <summary>
    /// The difference hash of Google Books' grey "image not available" picture,
    /// which it serves with a 200 instead of a cover for some volumes (zoom=0
    /// gave it for 2 of 15 volumes sampled on 2026-11-03).
    /// </summary>
    internal const ulong GooglePlaceholderHash = 0x0000482020800000UL;

    /// <summary>
    /// The most bits a hash may differ from <see cref="GooglePlaceholderHash"/>
    /// and still be that picture. Measured on 2026-11-03: the picture resized to
    /// 128 wide or re-encoded as JPEG is at most 1 bit away; a blank page is 5
    /// bits away and the nearest of 40 real Google Books covers and title pages 8.
    /// </summary>
    internal const int MaxPlaceholderDistance = 3;

    /// <summary>
    /// Why the image cannot be a cover, or null when it can. An image Skia
    /// cannot read is left to the caller.
    /// </summary>
    internal static string? Problem(byte[] data, int offset)
    {
        using var skData = SKData.CreateCopy(data.AsSpan(offset));
        using var codec = SKCodec.Create(skData);
        if (codec == null)
        {
            return null;
        }

        var width = codec.Info.Width;
        var height = codec.Info.Height;
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        if (width > height * MaxWidthOverHeight)
        {
            return $"{width}x{height} is too wide for a cover";
        }

        var hash = DifferenceHash(codec);
        if (hash is { } value
            && ulong.PopCount(value ^ GooglePlaceholderHash) <= MaxPlaceholderDistance)
        {
            return "Google Books' \"image not available\" picture";
        }

        return null;
    }

    /// <summary>
    /// A 64-bit difference hash: the image in 9 by 8 cells of mean brightness,
    /// one bit per cell clearly brighter than its right-hand neighbour. The same picture
    /// at any size gives the same or a near hash. Null when it cannot be decoded.
    /// </summary>
    internal static ulong? DifferenceHash(byte[] data)
    {
        using var skData = SKData.CreateCopy(data);
        using var codec = SKCodec.Create(skData);
        return codec == null ? null : DifferenceHash(codec);
    }

    private static ulong? DifferenceHash(SKCodec codec)
    {
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bitmap = new SKBitmap(info);
        var result = codec.GetPixels(info, bitmap.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            return null;
        }

        // Cells closer than this in brightness (out of 255) count as equal, so
        // compression noise on a white page cannot flip a bit.
        const double Flat = 4;
        const int Columns = 9;
        const int Rows = 8;
        var sums = new double[Rows, Columns];
        var counts = new int[Rows, Columns];
        var pixels = bitmap.GetPixelSpan();
        var stride = bitmap.RowBytes;
        for (var y = 0; y < info.Height; y++)
        {
            var row = y * Rows / info.Height;
            for (var x = 0; x < info.Width; x++)
            {
                var i = (y * stride) + (x * 4);
                var luma = (0.299 * pixels[i]) + (0.587 * pixels[i + 1]) + (0.114 * pixels[i + 2]);
                var column = x * Columns / info.Width;
                sums[row, column] += luma;
                counts[row, column]++;
            }
        }

        ulong hash = 0;
        for (var r = 0; r < Rows; r++)
        {
            for (var c = 0; c < Columns - 1; c++)
            {
                hash <<= 1;
                if (sums[r, c] / Math.Max(1, counts[r, c]) > (sums[r, c + 1] / Math.Max(1, counts[r, c + 1])) + Flat)
                {
                    hash |= 1;
                }
            }
        }

        return hash;
    }
}
