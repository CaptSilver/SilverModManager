using System.Buffers.Binary;
using System.Text;

namespace SilverModManager.Games.FromSoft.Tests.Formats.Fixtures;

/// <summary>
/// Builds archive-shaped BND4 files: 0x24-byte entry headers, then the names, a hash table and 0x10-aligned data.
/// The save-file shape (see <c>Saves/Bnd4.cs</c>) is a different layout and is built elsewhere.
/// </summary>
public static class Bnd4Fixture
{
    public const int HeaderSize = 0x40;
    public const int EntryHeaderSize = 0x24;

    public static readonly byte[] DefaultHashTable = [0xAB, 0xCD, 0xEF, 0x01, 0xAB, 0xCD, 0xEF, 0x01, 0xAB, 0xCD, 0xEF, 0x01, 0xAB, 0xCD, 0xEF, 0x01];

    /// <summary>The eight-byte field a regulation carries its build stamp in.</summary>
    public static readonly byte[] DefaultVersion = "07D7R6\0\0"u8.ToArray();

    public static byte[] Build(IReadOnlyList<(int Id, string Name, byte[] Data)> entries, byte[]? hashTable = null, byte[]? version = null)
    {
        hashTable ??= DefaultHashTable;
        version ??= DefaultVersion;

        var count = entries.Count;
        var namesOffset = HeaderSize + count * EntryHeaderSize;

        var names = new List<byte>();
        var nameOffsets = new List<int>();
        foreach (var (_, name, _) in entries)
        {
            nameOffsets.Add(namesOffset + names.Count);
            names.AddRange(Encoding.Unicode.GetBytes(name));
            names.AddRange(new byte[2]);
        }

        var hashOffset = namesOffset + names.Count;
        var dataOffset = (hashOffset + hashTable.Length + 0xF) & ~0xF;

        var header = new byte[HeaderSize];
        "BND4"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), 0x00010000);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0x0C), count);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(0x10), 0x40);
        version.AsSpan(0, 8).CopyTo(header.AsSpan(0x18));
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(0x20), EntryHeaderSize);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(0x28), dataOffset);
        header[0x30] = 1;
        header[0x31] = 0x74;
        header[0x32] = 4;
        header[0x33] = 0;
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(0x38), hashOffset);

        var headers = new byte[count * EntryHeaderSize];
        var blob = new List<byte>();
        var cursor = dataOffset;
        for (var i = 0; i < count; i++)
        {
            var (id, _, data) = entries[i];
            var e = headers.AsSpan(i * EntryHeaderSize, EntryHeaderSize);
            BinaryPrimitives.WriteInt32LittleEndian(e, 0x40);
            BinaryPrimitives.WriteInt32LittleEndian(e[4..], -1);
            BinaryPrimitives.WriteInt64LittleEndian(e[8..], data.Length);
            BinaryPrimitives.WriteInt64LittleEndian(e[0x10..], data.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(e[0x18..], (uint)cursor);
            BinaryPrimitives.WriteInt32LittleEndian(e[0x1C..], id);
            BinaryPrimitives.WriteInt32LittleEndian(e[0x20..], nameOffsets[i]);
            blob.AddRange(data);
            cursor += data.Length;
            // No padding after the last entry: shipped archives carry none.
            if (i < count - 1)
            {
                var pad = (-data.Length) & 0xF;
                blob.AddRange(new byte[pad]);
                cursor += pad;
            }
        }

        var middle = new byte[dataOffset - (namesOffset)];
        names.CopyTo(middle);
        hashTable.CopyTo(middle, names.Count);

        return [..header, ..headers, ..middle, ..blob];
    }

    /// <summary>The three-member archive the checked-in golden bytes were produced from.</summary>
    public static byte[] ThreeEntries() => Build(ThreeEntryList);

    public static readonly (int Id, string Name, byte[] Data)[] ThreeEntryList =
    [
        (10, "a.fmg", "AAA"u8.ToArray()),
        (20, "b.fmg", "BBBB"u8.ToArray()),
        (30, "c.fmg", Enumerable.Range(0, 40).Select(i => (byte)i).ToArray()),
    ];
}
