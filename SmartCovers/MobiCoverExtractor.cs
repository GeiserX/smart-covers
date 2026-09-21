using System.Buffers.Binary;

namespace SmartCovers;

/// <summary>
/// Pulls the cover image out of a MOBI / AZW e-book.
///
/// A MOBI file is a PalmDB container: a record table at the front, then the
/// records themselves. Record 0 carries the MOBI header, which says where the
/// image records begin (<c>first_image_index</c>) and — via the EXTH metadata
/// block, tag 201 — how far past that the cover sits. Everything here is plain
/// big-endian struct reading; no external dependency.
/// </summary>
internal static class MobiCoverExtractor
{
    private const int PalmHeaderSize = 78;
    private const int RecordEntrySize = 8;
    private const int MobiFirstImageIndexOffset = 0x6C;
    private const int MobiExthFlagsOffset = 0x80;
    private const int ExthPresentFlag = 0x40;
    private const int ExthTagCoverOffset = 201;
    private const int ExthTagThumbOffset = 202;

    // A cover past this is not a cover. Guards a crafted header from handing us
    // a multi-hundred-MB "image".
    private const int MaxImageBytes = 32 * 1024 * 1024;

    // How many image records to try when EXTH carries no cover offset.
    private const int MaxScannedImageRecords = 8;

    /// <summary>
    /// Reads the cover image bytes from a MOBI file, or returns null when the file
    /// is not a MOBI, carries no cover, or is malformed.
    /// </summary>
    internal static byte[]? TryExtractCover(Stream stream)
    {
        var offsets = ReadRecordOffsets(stream, out var fileLength);
        if (offsets == null || offsets.Length < 2)
        {
            return null;
        }

        var record0 = ReadRecord(stream, offsets, fileLength, 0);
        if (record0 == null || record0.Length < MobiExthFlagsOffset + 4)
        {
            return null;
        }

        // Record 0 is the PalmDOC header (16 bytes) followed by the MOBI header,
        // which is identified by its "MOBI" magic.
        if (record0[16] != (byte)'M' || record0[17] != (byte)'O'
            || record0[18] != (byte)'B' || record0[19] != (byte)'I')
        {
            return null;
        }

        var firstImageIndex = (int)ReadUInt32(record0, MobiFirstImageIndexOffset);
        if (firstImageIndex <= 0 || firstImageIndex >= offsets.Length)
        {
            return null;
        }

        foreach (var index in CandidateIndexes(record0, firstImageIndex, offsets.Length))
        {
            var bytes = ReadRecord(stream, offsets, fileLength, index);
            if (bytes != null && bytes.Length >= 1000 && bytes.Length <= MaxImageBytes
                && CoverImageProvider.DetectImageFormat(bytes).Format != null)
            {
                return bytes;
            }
        }

        return null;
    }

    /// <summary>
    /// The record indexes to try, best first: the EXTH-declared cover, then its
    /// thumbnail, then the first few image records as a blind fallback.
    /// </summary>
    private static IEnumerable<int> CandidateIndexes(byte[] record0, int firstImageIndex, int recordCount)
    {
        var seen = new HashSet<int>();

        foreach (var tag in new[] { ExthTagCoverOffset, ExthTagThumbOffset })
        {
            var offset = ReadExthUInt32(record0, tag);

            // 0xFFFFFFFF is the "not set" sentinel Kindlegen writes.
            if (offset is null or uint.MaxValue)
            {
                continue;
            }

            var index = firstImageIndex + (int)offset.Value;
            if (index > 0 && index < recordCount && seen.Add(index))
            {
                yield return index;
            }
        }

        for (var i = 0; i < MaxScannedImageRecords; i++)
        {
            var index = firstImageIndex + i;
            if (index >= recordCount)
            {
                yield break;
            }

            if (seen.Add(index))
            {
                yield return index;
            }
        }
    }

    /// <summary>
    /// Reads a 4-byte EXTH metadata value by tag, or null when absent.
    /// </summary>
    private static uint? ReadExthUInt32(byte[] record0, int tag)
    {
        if ((ReadUInt32(record0, MobiExthFlagsOffset) & ExthPresentFlag) == 0)
        {
            return null;
        }

        // The MOBI header starts at 16 and declares its own length; EXTH follows it.
        var mobiHeaderLength = (int)ReadUInt32(record0, 20);
        var exth = 16 + mobiHeaderLength;

        if (mobiHeaderLength <= 0 || exth + 12 > record0.Length
            || record0[exth] != (byte)'E' || record0[exth + 1] != (byte)'X'
            || record0[exth + 2] != (byte)'T' || record0[exth + 3] != (byte)'H')
        {
            return null;
        }

        var entryCount = (int)ReadUInt32(record0, exth + 8);
        var cursor = exth + 12;

        for (var i = 0; i < entryCount && cursor + 8 <= record0.Length; i++)
        {
            var entryTag = (int)ReadUInt32(record0, cursor);
            var entryLength = (int)ReadUInt32(record0, cursor + 4);

            // Length covers the 8-byte header; anything smaller would not advance.
            if (entryLength < 8 || cursor + entryLength > record0.Length)
            {
                return null;
            }

            if (entryTag == tag && entryLength == 12)
            {
                return ReadUInt32(record0, cursor + 8);
            }

            cursor += entryLength;
        }

        return null;
    }

    /// <summary>
    /// Reads the PalmDB record offset table.
    /// </summary>
    private static uint[]? ReadRecordOffsets(Stream stream, out long fileLength)
    {
        fileLength = stream.Length;

        if (fileLength < PalmHeaderSize + RecordEntrySize)
        {
            return null;
        }

        stream.Position = 0;
        var header = ReadExactly(stream, PalmHeaderSize);
        if (header == null)
        {
            return null;
        }

        // Offset 60 is the PalmDB type/creator: "BOOKMOBI" for MOBI e-books.
        var type = System.Text.Encoding.ASCII.GetString(header, 60, 8);
        if (!string.Equals(type, "BOOKMOBI", StringComparison.Ordinal)
            && !string.Equals(type, "TEXtREAd", StringComparison.Ordinal))
        {
            return null;
        }

        var recordCount = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(76, 2));
        if (recordCount < 2 || PalmHeaderSize + ((long)recordCount * RecordEntrySize) > fileLength)
        {
            return null;
        }

        var table = ReadExactly(stream, recordCount * RecordEntrySize);
        if (table == null)
        {
            return null;
        }

        var offsets = new uint[recordCount];
        for (var i = 0; i < recordCount; i++)
        {
            // Each 8-byte entry is a 4-byte offset, 1 attribute byte and a 3-byte id.
            offsets[i] = BinaryPrimitives.ReadUInt32BigEndian(table.AsSpan(i * RecordEntrySize, 4));
        }

        return offsets;
    }

    /// <summary>
    /// Reads record <paramref name="index"/>, which runs to the next record's offset.
    /// </summary>
    private static byte[]? ReadRecord(Stream stream, uint[] offsets, long fileLength, int index)
    {
        if (index < 0 || index >= offsets.Length)
        {
            return null;
        }

        long start = offsets[index];
        long end = index + 1 < offsets.Length ? offsets[index + 1] : fileLength;

        if (start >= end || end > fileLength || end - start > MaxImageBytes)
        {
            return null;
        }

        stream.Position = start;
        return ReadExactly(stream, (int)(end - start));
    }

    private static byte[]? ReadExactly(Stream stream, int count)
    {
        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            var n = stream.Read(buffer, read, count - read);
            if (n == 0)
            {
                return null;
            }

            read += n;
        }

        return buffer;
    }

    private static uint ReadUInt32(byte[] data, int offset)
        => offset + 4 <= data.Length
            ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4))
            : 0;
}
