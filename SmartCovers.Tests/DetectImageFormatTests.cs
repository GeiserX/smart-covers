using MediaBrowser.Model.Drawing;
using Xunit;

namespace SmartCovers.Tests;

public class DetectImageFormatTests
{
    [Fact]
    public void DetectImageFormat_Jpeg_Detected()
    {
        var data = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 };
        var (format, offset) = CoverImageProvider.DetectImageFormat(data);
        Assert.Equal(ImageFormat.Jpg, format);
        Assert.Equal(0, offset);
    }

    [Fact]
    public void DetectImageFormat_Png_Detected()
    {
        var data = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A };
        var (format, offset) = CoverImageProvider.DetectImageFormat(data);
        Assert.Equal(ImageFormat.Png, format);
        Assert.Equal(0, offset);
    }

    [Fact]
    public void DetectImageFormat_Gif_Detected()
    {
        var data = new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 };
        var (format, offset) = CoverImageProvider.DetectImageFormat(data);
        Assert.Equal(ImageFormat.Gif, format);
        Assert.Equal(0, offset);
    }

    [Fact]
    public void DetectImageFormat_WebP_Detected()
    {
        // RIFF....WEBP
        var data = new byte[]
        {
            0x52, 0x49, 0x46, 0x46,  // RIFF
            0x00, 0x00, 0x00, 0x00,  // size
            0x57, 0x45, 0x42, 0x50,  // WEBP
            0x00                      // extra byte
        };
        var (format, offset) = CoverImageProvider.DetectImageFormat(data);
        Assert.Equal(ImageFormat.Webp, format);
        Assert.Equal(0, offset);
    }

    [Fact]
    public void DetectImageFormat_LeadingNullBytes_SkippedCorrectly()
    {
        // 3 null bytes + JPEG magic
        var data = new byte[] { 0x00, 0x00, 0x00, 0xFF, 0xD8, 0xFF, 0xE0 };
        var (format, offset) = CoverImageProvider.DetectImageFormat(data);
        Assert.Equal(ImageFormat.Jpg, format);
        Assert.Equal(3, offset);
    }

    [Fact]
    public void DetectImageFormat_TooShort_ReturnsNull()
    {
        var data = new byte[] { 0xFF, 0xD8 };
        var (format, _) = CoverImageProvider.DetectImageFormat(data);
        Assert.Null(format);
    }

    [Fact]
    public void DetectImageFormat_UnknownFormat_ReturnsNull()
    {
        var data = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06 };
        var (format, _) = CoverImageProvider.DetectImageFormat(data);
        Assert.Null(format);
    }

    [Fact]
    public void DetectImageFormat_AllNullBytes_ReturnsNull()
    {
        var data = new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
        var (format, _) = CoverImageProvider.DetectImageFormat(data);
        Assert.Null(format);
    }

    [Fact]
    public void DetectImageFormat_PngWithPadding_DetectedAtCorrectOffset()
    {
        var data = new byte[] { 0x00, 0x00, 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A };
        var (format, offset) = CoverImageProvider.DetectImageFormat(data);
        Assert.Equal(ImageFormat.Png, format);
        Assert.Equal(2, offset);
    }

    [Fact]
    public void DetectImageFormat_EmptyArray_ReturnsNull()
    {
        var (format, _) = CoverImageProvider.DetectImageFormat(Array.Empty<byte>());
        Assert.Null(format);
    }
}
