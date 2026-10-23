using System.Buffers.Binary;
using System.Text;
using SilverModManager.Games.FromSoft.Formats;

namespace SilverModManager.Games.FromSoft.Tests.Formats.Fixtures;

/// <summary>
/// Assembles a TPF by hand, not through <see cref="Tpf.Write"/>, so the reader is tested against bytes laid out here:
/// header, entry table, name block padded to four, then the texture data end to end.
/// </summary>
public static class TpfFixture
{
    public const int HeaderSize = 0x10;
    public const int EntrySize = 0x14;

    public static byte[] Build(
        IReadOnlyList<(string Name, byte[] Data, byte Format)> textures,
        byte platform = 0,
        byte flag2 = 3,
        byte encoding = 1)
    {
        var tableEnd = HeaderSize + textures.Count * EntrySize;
        var names = new List<byte>();
        var nameOffsets = new List<int>();
        foreach (var (name, _, _) in textures)
        {
            nameOffsets.Add(tableEnd + names.Count);
            names.AddRange(encoding == 1
                ? Encoding.Unicode.GetBytes(name).Concat(new byte[] { 0, 0 })
                : ShiftJis.Encoding.GetBytes(name).Concat(new byte[] { 0 }));
        }

        var pad = (4 - names.Count % 4) % 4;
        var payload = textures.SelectMany(t => t.Data).ToArray();

        var raw = new byte[tableEnd + names.Count + pad + payload.Length];
        "TPF\0"u8.CopyTo(raw);
        BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(4), pad + payload.Length);
        BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(8), textures.Count);
        raw[0x0C] = platform;
        raw[0x0D] = flag2;
        raw[0x0E] = encoding;

        var cursor = tableEnd + names.Count + pad;
        for (var i = 0; i < textures.Count; i++)
        {
            var entry = raw.AsSpan(HeaderSize + i * EntrySize);
            BinaryPrimitives.WriteInt32LittleEndian(entry, cursor);
            BinaryPrimitives.WriteInt32LittleEndian(entry[4..], textures[i].Data.Length);
            entry[8] = textures[i].Format;
            entry[10] = 1;
            BinaryPrimitives.WriteInt32LittleEndian(entry[12..], nameOffsets[i]);
            textures[i].Data.CopyTo(raw, cursor);
            cursor += textures[i].Data.Length;
        }
        names.CopyTo(raw, tableEnd);
        return raw;
    }
}
