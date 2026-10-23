using System.Buffers.Binary;
using System.Text;

namespace SilverModManager.Games.FromSoft.Formats;

/// <summary>One texture in a TPF: its name, the DDS bytes as stored, and the entry flags the game reads them with.</summary>
public sealed record Texture(string Name, byte[] Data, byte Format, byte Type, byte Mipmaps, byte Flags1);

/// <summary>A parsed TPF. The header fields beyond the textures are kept so a rewrite reproduces them.</summary>
public sealed record TpfArchive(IReadOnlyList<Texture> Textures, byte Platform, byte Flag2, byte Encoding)
{
    public TpfArchive WithTextures(IEnumerable<Texture> textures) => this with { Textures = [.. textures] };
}

/// <summary>
/// TPF: the texture container behind the game's menu icons. Flat by construction: header, entry table, name block, then
/// every texture's bytes end to end in table order. Everything in it is either copied verbatim or recomputed from the
/// textures, so unlike a binder it can be written from scratch.
/// </summary>
public static class Tpf
{
    private const int HeaderSize = 0x10;
    private const int EntrySize = 0x14;

    // Measured, not assumed: one shipped file's name block ends already aligned, another ends 2 bytes short of a
    // multiple of four, and only this alignment reproduces both byte-for-byte. Every texture is addressed by an
    // explicit offset, so the padding is cosmetic to the game.
    private const int NameAlign = 4;

    private const byte Utf16Names = 1;

    private static readonly UnicodeEncoding Utf16 = new(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true);

    /// <summary>Parses a TPF.</summary>
    /// <exception cref="TpfException">The data is malformed or uses a layout this reader does not parse. These files come from third-party mods, so every count and offset is checked.</exception>
    public static TpfArchive Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize || !data[..4].SequenceEqual("TPF\0"u8))
            throw new TpfException("not a TPF archive (bad magic)");

        var count = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
        var platform = data[0x0C];
        var flag2 = data[0x0D];
        var encoding = data[0x0E];

        // Rejected before the loop, so a bogus count cannot walk the entry reads off the end of the buffer.
        if (HeaderSize + (long)count * EntrySize > data.Length)
            throw new TpfException($"TPF claims {count} textures, which doesn't fit in {data.Length} bytes");
        if (platform != 0)
            throw new TpfException($"TPF platform {platform} isn't PC; its entry headers carry extra fields this reader doesn't parse");

        var textures = new List<Texture>((int)count);
        for (var i = 0; i < count; i++)
        {
            var entry = data[(HeaderSize + i * EntrySize)..];
            var dataOffset = BinaryPrimitives.ReadUInt32LittleEndian(entry);
            var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]);
            var nameOffset = BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]);
            var hasFloatStruct = BinaryPrimitives.ReadUInt32LittleEndian(entry[16..]);

            if (hasFloatStruct != 0)
                throw new TpfException($"texture {i} carries a FloatStruct, which makes the entry stride variable; refusing rather than misreading every entry after it");
            if ((long)dataOffset + dataSize > data.Length)
                throw new TpfException($"texture {i} data runs past the end of the file ({dataOffset}+{dataSize} > {data.Length})");

            textures.Add(new Texture(
                DecodeName(data, nameOffset, encoding),
                data.Slice((int)dataOffset, (int)dataSize).ToArray(),
                entry[8], entry[9], entry[10], entry[11]));
        }
        return new TpfArchive(textures, platform, flag2, encoding);
    }

    /// <summary>
    /// Serialises an archive. Offsets are recomputed from scratch: appending a texture grows the entry table and the
    /// name block, so every data offset behind it moves. Reproduces a shipped file byte-for-byte when nothing changed.
    /// </summary>
    /// <exception cref="TpfException">A texture name cannot be encoded in the archive's name encoding.</exception>
    public static byte[] Write(TpfArchive archive)
    {
        var textures = archive.Textures;
        var tableEnd = HeaderSize + textures.Count * EntrySize;

        var names = new MemoryStream();
        var nameOffsets = new int[textures.Count];
        for (var i = 0; i < textures.Count; i++)
        {
            nameOffsets[i] = tableEnd + (int)names.Length;
            names.Write(EncodeName(textures[i].Name, archive.Encoding));
        }

        var pad = (NameAlign - (int)names.Length % NameAlign) % NameAlign;
        var payloadSize = textures.Sum(t => t.Data.Length);
        var raw = new byte[tableEnd + (int)names.Length + pad + payloadSize];

        "TPF\0"u8.CopyTo(raw);
        // dataSize spans everything after the name block, so the alignment padding counts towards it; that is what
        // both shipped files record.
        BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(4), pad + payloadSize);
        BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(8), textures.Count);
        raw[0x0C] = archive.Platform;
        raw[0x0D] = archive.Flag2;
        raw[0x0E] = archive.Encoding;

        names.ToArray().CopyTo(raw, tableEnd);
        var cursor = tableEnd + (int)names.Length + pad;
        for (var i = 0; i < textures.Count; i++)
        {
            var texture = textures[i];
            var entry = raw.AsSpan(HeaderSize + i * EntrySize);
            BinaryPrimitives.WriteInt32LittleEndian(entry, cursor);
            BinaryPrimitives.WriteInt32LittleEndian(entry[4..], texture.Data.Length);
            entry[8] = texture.Format;
            entry[9] = texture.Type;
            entry[10] = texture.Mipmaps;
            entry[11] = texture.Flags1;
            BinaryPrimitives.WriteInt32LittleEndian(entry[12..], nameOffsets[i]);
            texture.Data.CopyTo(raw, cursor);
            cursor += texture.Data.Length;
        }
        return raw;
    }

    private static string DecodeName(ReadOnlySpan<byte> data, uint offset, byte encoding)
    {
        if (offset >= data.Length)
            throw new TpfException($"texture name offset {offset} is outside the file");

        // Strings are scanned for the real terminator: two zero bytes on an even boundary for UTF-16, because a single
        // zero byte is the high half of the first ASCII character.
        if (encoding == Utf16Names)
            return Utf16Strings.ReadZ(data, (int)offset, message => new TpfException($"texture name: {message}"));

        var tail = data[(int)offset..];
        var end = tail.IndexOf((byte)0);
        if (end < 0)
            throw new TpfException("unterminated texture name");
        try
        {
            return ShiftJis.Encoding.GetString(tail[..end]);
        }
        catch (DecoderFallbackException e)
        {
            throw new TpfException($"texture name at {offset} is not valid Shift_JIS: {e.Message}");
        }
    }

    private static byte[] EncodeName(string name, byte encoding)
    {
        try
        {
            return encoding == Utf16Names
                ? [.. Utf16.GetBytes(name), 0, 0]
                : [.. ShiftJis.Encoding.GetBytes(name), 0];
        }
        catch (EncoderFallbackException e)
        {
            throw new TpfException($"texture name '{name}' can't be stored in this archive's name encoding: {e.Message}");
        }
    }
}
