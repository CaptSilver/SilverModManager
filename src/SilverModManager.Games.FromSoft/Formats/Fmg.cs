using System.Buffers.Binary;
using System.Text;

namespace SilverModManager.Games.FromSoft.Formats;

/// <summary>
/// FMG: the message table inside a msgbnd, mapping a numeric text id to a string. Only version 2 (64-bit string
/// offsets) is read or written. Ids are stored as runs of consecutive ids plus a flat offset array, so sparse ids cost
/// one range per run.
/// </summary>
public static class Fmg
{
    private const byte Version = 2;
    private const int HeaderSize = 0x28;
    private const int RangeSize = 0x10;

    private static readonly UnicodeEncoding Utf16 = new(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Parses an FMG into id to text. A null text is an id that exists with no string, which is not the same as the id
    /// being missing.
    /// </summary>
    /// <exception cref="FmgException">The data is malformed. These files come from third-party mods, so every header field and offset is checked.</exception>
    public static Dictionary<int, string?> Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize)
            throw new FmgException("FMG is shorter than its header");
        if (data[2] != Version)
            throw new FmgException($"unsupported FMG version {data[2]}, expected {Version}");

        var rangeCount = BinaryPrimitives.ReadInt32LittleEndian(data[0x0C..]);
        var stringCount = BinaryPrimitives.ReadInt32LittleEndian(data[0x10..]);
        var offsetsOffset = BinaryPrimitives.ReadInt64LittleEndian(data[0x18..]);

        // A negative count would make the loop below run zero times and hand back an empty table for a non-empty file.
        if (rangeCount < 0)
            throw new FmgException($"FMG range_count is negative: {rangeCount}");
        if ((long)rangeCount * RangeSize > data.Length - HeaderSize)
            throw new FmgException($"FMG range table ({rangeCount} ranges) runs past the end of the {data.Length}-byte buffer");

        // Written so that no sum can overflow, whatever the header claims.
        if (stringCount < 0 || offsetsOffset < 0 || offsetsOffset > data.Length || (long)stringCount * 8 > data.Length - offsetsOffset)
            throw new FmgException($"FMG offset table (offset {offsetsOffset}, {stringCount} entries) runs outside the {data.Length}-byte buffer");

        var entries = new Dictionary<int, string?>();
        for (var r = 0; r < rangeCount; r++)
        {
            var range = data[(HeaderSize + r * RangeSize)..];
            var index = BinaryPrimitives.ReadInt32LittleEndian(range);
            var first = BinaryPrimitives.ReadInt32LittleEndian(range[4..]);
            var last = BinaryPrimitives.ReadInt32LittleEndian(range[8..]);
            if (last < first)
                throw new FmgException($"FMG range {r} is reversed: first={first}, last={last}");

            // Long, so a range ending at int.MaxValue terminates instead of wrapping.
            for (long id = first; id <= last; id++)
            {
                var slot = index + (id - first);
                if (slot < 0 || slot >= stringCount)
                    throw new FmgException($"FMG range {r} points past the offset table");

                var stringOffset = BinaryPrimitives.ReadInt64LittleEndian(data[(int)(offsetsOffset + slot * 8)..]);
                if (stringOffset < 0)
                    throw new FmgException($"FMG string offset for id {id} is negative: {stringOffset}");
                if (stringOffset >= data.Length)
                    throw new FmgException($"FMG string offset for id {id} is past the end of the {data.Length}-byte buffer");

                entries[(int)id] = stringOffset == 0
                    ? null
                    : Utf16Strings.ReadZ(data, (int)stringOffset, m => new FmgException($"{m} in FMG"));
            }
        }
        return entries;
    }

    /// <summary>Serialises id to text as FMG version 2, sharing repeated strings the way the game's own files do.</summary>
    /// <exception cref="FmgException">A text is not valid UTF-16.</exception>
    public static byte[] Write(IReadOnlyDictionary<int, string?> entries)
    {
        var ids = entries.Keys.Order().ToArray();
        var ranges = ConsecutiveRanges(ids);
        var offsetsOffset = HeaderSize + ranges.Count * RangeSize;
        var stringsOffset = offsetsOffset + ids.Length * 8;

        var blob = new MemoryStream();
        var offsets = new long[ids.Length];
        var shared = new Dictionary<string, long>(StringComparer.Ordinal);
        for (var i = 0; i < ids.Length; i++)
        {
            var text = entries[ids[i]];
            if (text is null) continue; // offset 0 reads back as "no text"
            if (!shared.TryGetValue(text, out var position))
            {
                position = stringsOffset + blob.Length;
                shared[text] = position;
                byte[] encoded;
                try
                {
                    encoded = Utf16.GetBytes(text);
                }
                catch (EncoderFallbackException e)
                {
                    throw new FmgException($"text for id {ids[i]} is not valid UTF-16: {e.Message}");
                }
                blob.Write(encoded);
                blob.Write([0, 0]);
            }
            offsets[i] = position;
        }

        var output = new byte[stringsOffset + blob.Length];
        output[2] = Version;
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(4), output.Length);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(0x08), 1);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(0x0C), ranges.Count);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(0x10), ids.Length);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(0x14), 0xFF);
        BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(0x18), offsetsOffset);

        var slot = 0;
        for (var r = 0; r < ranges.Count; r++)
        {
            var range = output.AsSpan(HeaderSize + r * RangeSize);
            BinaryPrimitives.WriteInt32LittleEndian(range, slot);
            BinaryPrimitives.WriteInt32LittleEndian(range[4..], ranges[r].First);
            BinaryPrimitives.WriteInt32LittleEndian(range[8..], ranges[r].Last);
            slot += (int)((long)ranges[r].Last - ranges[r].First + 1);
        }
        for (var i = 0; i < offsets.Length; i++)
            BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(offsetsOffset + i * 8), offsets[i]);
        blob.ToArray().CopyTo(output, stringsOffset);
        return output;
    }

    private static List<(int First, int Last)> ConsecutiveRanges(int[] sortedIds)
    {
        var ranges = new List<(int, int)>();
        var i = 0;
        while (i < sortedIds.Length)
        {
            var j = i;
            // Long, so int.MaxValue + 1 does not wrap onto int.MinValue.
            while (j + 1 < sortedIds.Length && sortedIds[j + 1] == (long)sortedIds[j] + 1) j++;
            ranges.Add((sortedIds[i], sortedIds[j]));
            i = j + 1;
        }
        return ranges;
    }
}
