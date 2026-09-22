using System.Buffers.Binary;
using System.Text;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace SmartCovers.Tests;

/// <summary>
/// MOBI / AZW e-books carry their cover in a PalmDB image record that the EXTH
/// metadata block points at. The fixtures here are built to that layout rather
/// than shipped as binaries, so the test states the format it relies on.
/// </summary>
public class MobiCoverTests
{
    private const int PalmHeaderSize = 78;

    private static byte[] FakeJpeg(int size, byte marker)
    {
        var data = new byte[size];
        data[0] = 0xFF; data[1] = 0xD8; data[2] = 0xFF; data[3] = 0xE0;
        data[^1] = marker;
        return data;
    }

    /// <summary>
    /// Builds a MOBI whose records are: 0 the headers, 1 text, then the images.
    /// </summary>
    /// <param name="images">The image records, starting at <c>first_image_index</c>.</param>
    /// <param name="coverOffset">EXTH tag 201, or null to omit the EXTH block.</param>
    /// <param name="palmType">The 8-byte PalmDB type/creator, "BOOKMOBI" for an e-book.</param>
    private static byte[] BuildMobi(IReadOnlyList<byte[]> images, uint? coverOffset, string palmType = "BOOKMOBI")
    {
        const int MobiHeaderLength = 232;
        const int FirstImageIndex = 2;

        var record0 = new byte[248 + (coverOffset.HasValue ? 24 : 0)];
        Encoding.ASCII.GetBytes("MOBI").CopyTo(record0, 16);
        BinaryPrimitives.WriteUInt32BigEndian(record0.AsSpan(20, 4), MobiHeaderLength);
        BinaryPrimitives.WriteUInt32BigEndian(record0.AsSpan(0x6C, 4), FirstImageIndex);

        if (coverOffset.HasValue)
        {
            // Bit 0x40 of the header's flags at 0x80 announces the EXTH block,
            // which starts right after the MOBI header.
            BinaryPrimitives.WriteUInt32BigEndian(record0.AsSpan(0x80, 4), 0x40);

            var exth = 16 + MobiHeaderLength;
            Encoding.ASCII.GetBytes("EXTH").CopyTo(record0, exth);
            BinaryPrimitives.WriteUInt32BigEndian(record0.AsSpan(exth + 4, 4), 24);
            BinaryPrimitives.WriteUInt32BigEndian(record0.AsSpan(exth + 8, 4), 1);
            BinaryPrimitives.WriteUInt32BigEndian(record0.AsSpan(exth + 12, 4), 201);
            BinaryPrimitives.WriteUInt32BigEndian(record0.AsSpan(exth + 16, 4), 12);
            BinaryPrimitives.WriteUInt32BigEndian(record0.AsSpan(exth + 20, 4), coverOffset.Value);
        }

        List<byte[]> records = [record0, Encoding.ASCII.GetBytes("book text"), .. images];

        var header = new byte[PalmHeaderSize];
        Encoding.ASCII.GetBytes(palmType).CopyTo(header, 60);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(76, 2), (ushort)records.Count);

        var tableSize = records.Count * 8;
        var table = new byte[tableSize];
        var offset = (uint)(PalmHeaderSize + tableSize);

        for (var i = 0; i < records.Count; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(i * 8, 4), offset);
            offset += (uint)records[i].Length;
        }

        var file = new MemoryStream();
        file.Write(header);
        file.Write(table);
        foreach (var record in records)
        {
            file.Write(record);
        }

        return file.ToArray();
    }

    [Fact]
    public void TryExtractCover_ExthCoverOffset_ReturnsThatRecord()
    {
        var decoy = FakeJpeg(2000, 0x11);
        var cover = FakeJpeg(3000, 0x22);

        // first_image_index is 2 and EXTH 201 says +1, so the cover is record 3.
        var mobi = BuildMobi([decoy, cover], coverOffset: 1);

        using var stream = new MemoryStream(mobi);
        var result = MobiCoverExtractor.TryExtractCover(stream);

        Assert.NotNull(result);
        Assert.Equal(3000, result!.Length);
        Assert.Equal(0x22, result[^1]);
    }

    [Fact]
    public void TryExtractCover_NoExth_FallsBackToTheFirstImageRecord()
    {
        var first = FakeJpeg(2000, 0x33);

        var mobi = BuildMobi([first, FakeJpeg(3000, 0x44)], coverOffset: null);

        using var stream = new MemoryStream(mobi);
        var result = MobiCoverExtractor.TryExtractCover(stream);

        Assert.NotNull(result);
        Assert.Equal(0x33, result![^1]);
    }

    [Fact]
    public void TryExtractCover_CoverRecordIsNotAnImage_SkipsToTheNextCandidate()
    {
        var notAnImage = new byte[2000];
        var realImage = FakeJpeg(3000, 0x55);

        // EXTH points at record 3, which is junk; the blind scan then finds record 2.
        var mobi = BuildMobi([realImage, notAnImage], coverOffset: 1);

        using var stream = new MemoryStream(mobi);
        var result = MobiCoverExtractor.TryExtractCover(stream);

        Assert.NotNull(result);
        Assert.Equal(0x55, result![^1]);
    }

    [Fact]
    public async Task GetImage_MobiBook_ExtractsTheCoverWithoutAskingOnline()
    {
        // Before this branch existed, .mobi fell through to "no image" and the file
        // had no local cover at all.
        var dir = Path.Combine(Path.GetTempPath(), $"smartcovers-mobi-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        try
        {
            var path = Path.Combine(dir, "Quiet Harbour - Ana Ruiz.mobi");
            await File.WriteAllBytesAsync(path, BuildMobi([FakeJpeg(3000, 0x99)], coverOffset: 0));

            var handler = new MockHttpHandler();
            var provider = new CoverImageProvider(
                Mock.Of<ILogger<CoverImageProvider>>(),
                new OnlineCoverFetcher(Mock.Of<ILogger<OnlineCoverFetcher>>(), new HttpClient(handler)),
                () => false);

            var item = new Mock<Book>();
            item.SetupGet(i => i.Path).Returns(path);
            item.SetupGet(i => i.Name).Returns("Quiet Harbour");

            var result = await provider.GetImage(item.Object, ImageType.Primary, CancellationToken.None);

            Assert.True(result.HasImage);
            Assert.Equal(ImageFormat.Jpg, result.Format);
            Assert.Equal(3000, result.Stream!.Length);
            Assert.Empty(handler.RequestedUrls);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void TryExtractCover_Cancelled_StopsInsteadOfReadingEveryCandidate()
    {
        // The extraction runs synchronously on a pool thread, so the token has to
        // be honoured inside the read loop or a shutdown waits for it.
        var mobi = BuildMobi([FakeJpeg(3000, 0xDD)], coverOffset: 0);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        using var stream = new MemoryStream(mobi);

        Assert.ThrowsAny<OperationCanceledException>(
            () => MobiCoverExtractor.TryExtractCover(stream, cts.Token));
    }

    [Fact]
    public void TryExtractCover_NotAMobi_ReturnsNull()
    {
        var mobi = BuildMobi([FakeJpeg(3000, 0x66)], coverOffset: 0, palmType: "TEXtTEXt");

        using var stream = new MemoryStream(mobi);
        Assert.Null(MobiCoverExtractor.TryExtractCover(stream));
    }

    [Theory]
    // Record 0 must carry the MOBI magic; without it this is some other PalmDB file.
    [InlineData("magic")]
    // first_image_index outside the record table points nowhere.
    [InlineData("imageIndexZero")]
    [InlineData("imageIndexPastEnd")]
    // A record offset table that runs past the end of the file.
    [InlineData("offsetPastEnd")]
    // A PalmDB with a single record cannot hold headers and an image.
    [InlineData("oneRecordOnly")]
    public void TryExtractCover_MalformedFile_ReturnsNullRatherThanThrowing(string damage)
    {
        var mobi = BuildMobi([FakeJpeg(3000, 0xAA)], coverOffset: 0);
        var record0Start = 78 + (3 * 8);

        switch (damage)
        {
            case "magic":
                mobi[record0Start + 16] = (byte)'X';
                break;
            case "imageIndexZero":
                BinaryPrimitives.WriteUInt32BigEndian(mobi.AsSpan(record0Start + 0x6C, 4), 0);
                break;
            case "imageIndexPastEnd":
                BinaryPrimitives.WriteUInt32BigEndian(mobi.AsSpan(record0Start + 0x6C, 4), 999);
                break;
            case "offsetPastEnd":
                BinaryPrimitives.WriteUInt16BigEndian(mobi.AsSpan(76, 2), 9999);
                break;
            case "oneRecordOnly":
                BinaryPrimitives.WriteUInt16BigEndian(mobi.AsSpan(76, 2), 1);
                break;
        }

        using var stream = new MemoryStream(mobi);
        Assert.Null(MobiCoverExtractor.TryExtractCover(stream));
    }

    [Theory]
    // An EXTH entry whose declared length cannot advance the cursor.
    [InlineData("exthZeroLengthEntry")]
    // The EXTH flag is set but no EXTH block actually follows.
    [InlineData("exthMissingMagic")]
    public void TryExtractCover_UnreadableExth_StillFindsTheImageByScanning(string damage)
    {
        var mobi = BuildMobi([FakeJpeg(3000, 0xCC)], coverOffset: 0);
        var record0Start = 78 + (3 * 8);

        if (damage == "exthZeroLengthEntry")
        {
            BinaryPrimitives.WriteUInt32BigEndian(mobi.AsSpan(record0Start + 16 + 232 + 16, 4), 0);
        }
        else
        {
            mobi[record0Start + 16 + 232] = (byte)'X';
        }

        using var stream = new MemoryStream(mobi);
        var result = MobiCoverExtractor.TryExtractCover(stream);

        // Losing the metadata costs precision, not the cover: the scan over the
        // image records still finds it.
        Assert.NotNull(result);
        Assert.Equal(0xCC, result![^1]);
    }

    [Fact]
    public void TryExtractCover_StreamEndsBeforeADeclaredRecord_ReturnsNull()
    {
        var mobi = BuildMobi([FakeJpeg(3000, 0xBB)], coverOffset: 0);

        // Keep the header and table intact but drop the tail the table promises.
        using var stream = new MemoryStream(mobi.AsSpan(0, mobi.Length - 2500).ToArray());
        Assert.Null(MobiCoverExtractor.TryExtractCover(stream));
    }

    [Fact]
    public async Task GetImage_MobiWithNoUsableImage_YieldsNoLocalCover()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"smartcovers-mobi-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        try
        {
            // The only "image" record is junk, so nothing local can be offered.
            var path = Path.Combine(dir, "Quiet Harbour.mobi");
            await File.WriteAllBytesAsync(path, BuildMobi([new byte[2000]], coverOffset: 0));

            var handler = new MockHttpHandler();
            handler.AddJsonResponse("openlibrary.org/search.json", new { docs = Array.Empty<object>() });
            handler.AddJsonResponse("googleapis.com/books", new { totalItems = 0 });

            var provider = new CoverImageProvider(
                Mock.Of<ILogger<CoverImageProvider>>(),
                new OnlineCoverFetcher(Mock.Of<ILogger<OnlineCoverFetcher>>(), new HttpClient(handler)),
                () => false);

            var item = new Mock<Book>();
            item.SetupGet(i => i.Path).Returns(path);
            item.SetupGet(i => i.Name).Returns("Quiet Harbour");

            var result = await provider.GetImage(item.Object, ImageType.Primary, CancellationToken.None);

            Assert.False(result.HasImage);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void TryExtractCover_TruncatedFile_ReturnsNull()
    {
        var mobi = BuildMobi([FakeJpeg(3000, 0x77)], coverOffset: 0);

        using var stream = new MemoryStream(mobi.AsSpan(0, 40).ToArray());
        Assert.Null(MobiCoverExtractor.TryExtractCover(stream));
    }

    [Fact]
    public void TryExtractCover_CoverBelowThePlaceholderThreshold_IsRejected()
    {
        // Under 1000 bytes is a placeholder, not a cover.
        var mobi = BuildMobi([FakeJpeg(200, 0x88)], coverOffset: 0);

        using var stream = new MemoryStream(mobi);
        Assert.Null(MobiCoverExtractor.TryExtractCover(stream));
    }
}
