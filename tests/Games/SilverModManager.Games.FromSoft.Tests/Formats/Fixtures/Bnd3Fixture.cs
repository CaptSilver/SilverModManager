using System.Buffers.Binary;
using SilverModManager.Games.FromSoft.Formats;

namespace SilverModManager.Games.FromSoft.Tests.Formats.Fixtures;

/// <summary>
/// Builds BND3 archives: a 0x20-byte header, the entry headers, null-terminated Shift_JIS names and 0x10-aligned
/// data. The format flags choose which optional entry fields exist, so the same builder yields every stride. Flags are
/// passed decoded and stored the way the games store them: least significant bit first in a little-endian archive,
/// in bit order (with 0x0E set) in a big-endian one.
/// </summary>
public static class Bnd3Fixture
{
    public const int HeaderSize = 0x20;

    public const byte Ids = 0x02;
    public const byte Names1 = 0x04;
    public const byte Names2 = 0x08;
    public const byte Names = Names1 | Names2;
    public const byte LongOffsets = 0x10;
    public const byte Compression = 0x20;

    // What shipped archives carry: ids, names and an uncompressed size beside each stored size.
    public const byte DefaultFlags = Ids | Names | Compression;

    // The entry flags shipped archives give an uncompressed member; 0x01 would mark it compressed.
    public const byte MemberFlags = 0x02;

    private static readonly byte[] Version = "07D7R6\0\0"u8.ToArray();

    /// <summary>Bytes in one entry header for these format flags.</summary>
    public static int EntryHeaderSize(byte formatFlags) =>
        8 + ((formatFlags & LongOffsets) != 0 ? 8 : 4)
          + ((formatFlags & Ids) != 0 ? 4 : 0)
          + ((formatFlags & Names) != 0 ? 4 : 0)
          + ((formatFlags & Compression) != 0 ? 4 : 0);

    public static byte[] Build(IReadOnlyList<(int Id, string Name, byte[] Data)> entries, byte formatFlags = DefaultFlags, bool bigEndian = false, IReadOnlyList<byte>? entryFlags = null)
    {
        var stride = EntryHeaderSize(formatFlags);
        var hasNames = (formatFlags & Names) != 0;
        var count = entries.Count;
        var namesOffset = HeaderSize + count * stride;

        var names = new List<byte>();
        var nameOffsets = new List<int>();
        foreach (var (_, name, _) in entries)
        {
            nameOffsets.Add(namesOffset + names.Count);
            if (!hasNames) continue;
            names.AddRange(ShiftJis.Encoding.GetBytes(name));
            names.Add(0);
        }

        var dataOffset = (namesOffset + names.Count + 0xF) & ~0xF;
        var blob = new List<byte>();
        var cursor = dataOffset;
        var headers = new byte[count * stride];
        for (var i = 0; i < count; i++)
        {
            var (id, _, data) = entries[i];
            var w = new Writer(headers.AsSpan(i * stride, stride), bigEndian);
            w.U8(Store(entryFlags?[i] ?? MemberFlags, bigEndian));
            w.Seek(4);
            w.I32(data.Length);
            if ((formatFlags & LongOffsets) != 0) w.I64(cursor); else w.I32(cursor);
            if ((formatFlags & Ids) != 0) w.I32(id);
            if (hasNames) w.I32(nameOffsets[i]);
            if ((formatFlags & Compression) != 0) w.I32(data.Length);

            blob.AddRange(data);
            cursor += data.Length;
            // No padding after the last entry: shipped archives carry none.
            if (i < count - 1)
            {
                var pad = -data.Length & 0xF;
                blob.AddRange(new byte[pad]);
                cursor += pad;
            }
        }

        var header = new byte[HeaderSize];
        var hw = new Writer(header, bigEndian);
        "BND3"u8.CopyTo(header);
        Version.CopyTo(header.AsSpan(4));
        header[0x0C] = Store(formatFlags, bigEndian);
        header[0x0D] = (byte)(bigEndian ? 1 : 0);
        header[0x0E] = (byte)(bigEndian ? 1 : 0);
        hw.Seek(0x10);
        hw.I32(count);
        hw.I32(namesOffset + names.Count);

        return [..header, ..headers, ..names, ..new byte[dataOffset - namesOffset - names.Count], ..blob];
    }

    /// <summary>A flags byte as stored: bit order in a big-endian archive, least significant bit first otherwise.</summary>
    public static byte Store(byte flags, bool bigEndian)
    {
        if (bigEndian) return flags;
        var reversed = 0;
        for (var bit = 0; bit < 8; bit++)
            if ((flags & (1 << bit)) != 0) reversed |= 0x80 >> bit;
        return (byte)reversed;
    }

    public static readonly (int Id, string Name, byte[] Data)[] ThreeEntryList =
    [
        (10, "a.fmg", "AAA"u8.ToArray()),
        (20, "b.fmg", "BBBB"u8.ToArray()),
        (30, "c.fmg", Enumerable.Range(0, 40).Select(i => (byte)i).ToArray()),
    ];

    private ref struct Writer(Span<byte> buffer, bool bigEndian)
    {
        private readonly Span<byte> _buffer = buffer;
        private int _pos;

        public void Seek(int pos) => _pos = pos;

        public void U8(byte value) => _buffer[_pos++] = value;

        public void I32(int value)
        {
            if (bigEndian) BinaryPrimitives.WriteInt32BigEndian(_buffer[_pos..], value);
            else BinaryPrimitives.WriteInt32LittleEndian(_buffer[_pos..], value);
            _pos += 4;
        }

        public void I64(long value)
        {
            if (bigEndian) BinaryPrimitives.WriteInt64BigEndian(_buffer[_pos..], value);
            else BinaryPrimitives.WriteInt64LittleEndian(_buffer[_pos..], value);
            _pos += 8;
        }
    }
}
