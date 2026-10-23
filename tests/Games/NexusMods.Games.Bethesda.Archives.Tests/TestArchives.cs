using System.IO.Compression;
using System.Text;
using K4os.Compression.LZ4.Streams;

namespace NexusMods.Games.Bethesda.Archives.Tests;

/// <summary>
/// Writes minimal archives from the on-disk layouts documented by the UESP wiki
/// (https://en.uesp.net/wiki/Skyrim_Mod:Archive_File_Format for BSA v103-105 and
/// https://en.uesp.net/wiki/Morrowind_Mod:BSA_File_Format for Morrowind). Nothing here shares code with the
/// reader, so a misreading of the format on one side does not hide on the other. Names are written as
/// Latin-1, so a test spells a raw name byte as the char with the same value.
/// </summary>
internal static class TestArchives
{
    public record Tes3Entry(string Name, byte[] Data);

    /// <param name="Compressed">Whether the bytes on disk are compressed. The writer sets the flip flag when
    /// this differs from the archive default.</param>
    public record Tes5Entry(string Folder, string Name, byte[] Data, bool Compressed);

    public const uint DefaultCompressed = 0x4;
    public const uint EmbeddedNames = 0x100;
    public const uint FolderAndFileNames = 0x3;

    public static byte[] Tes3(params Tes3Entry[] entries)
    {
        var names = new MemoryStream();
        var nameOffsets = new List<uint>();
        foreach (var entry in entries)
        {
            nameOffsets.Add((uint)names.Length);
            names.Write(Encoding.Latin1.GetBytes(entry.Name));
            names.WriteByte(0);
        }

        var count = (uint)entries.Length;
        // Hash table sits right after the name block; its offset is relative to the end of the 12 byte header.
        var hashOffset = count * 8 + count * 4 + (uint)names.Length;

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(0x100u);
        w.Write(hashOffset);
        w.Write(count);

        uint offset = 0;
        foreach (var entry in entries)
        {
            w.Write((uint)entry.Data.Length);
            w.Write(offset);
            offset += (uint)entry.Data.Length;
        }

        foreach (var nameOffset in nameOffsets) w.Write(nameOffset);
        w.Write(names.ToArray());

        for (var i = 0; i < entries.Length; i++)
        {
            w.Write(0xAA000000u + (uint)i);
            w.Write(0xBB000000u + (uint)i);
        }

        foreach (var entry in entries) w.Write(entry.Data);
        return ms.ToArray();
    }

    public static byte[] Tes5(uint version, uint archiveFlags, params Tes5Entry[] entries)
    {
        var folders = entries.GroupBy(e => e.Folder).ToList();
        var longForm = version == 105;
        var folderRecordLength = longForm ? 24 : 16;
        var hasFolderNames = (archiveFlags & 0x1) != 0;
        var hasFileNames = (archiveFlags & 0x2) != 0;
        var defaultCompressed = (archiveFlags & DefaultCompressed) != 0;
        var embedNames = (archiveFlags & EmbeddedNames) != 0 && version >= 104;

        // Everything that lands in the file record blocks, to size the layout before writing offsets.
        long blocksLength = 0;
        foreach (var folder in folders)
        {
            if (hasFolderNames) blocksLength += 1 + folder.Key.Length + 1;
            blocksLength += folder.Count() * 16;
        }

        var fileNames = new MemoryStream();
        foreach (var entry in folders.SelectMany(f => f))
        {
            fileNames.Write(Encoding.Latin1.GetBytes(entry.Name));
            fileNames.WriteByte(0);
        }

        var nameBlockLength = hasFileNames ? (int)fileNames.Length : 0;
        var dataStart = 36 + folders.Count * folderRecordLength + blocksLength + nameBlockLength;

        // Encode each file's on-disk bytes and place it.
        var payloads = new List<(byte[] Bytes, uint SizeField)>();
        foreach (var entry in folders.SelectMany(f => f))
        {
            var prefix = new MemoryStream();
            if (embedNames)
            {
                var full = Encoding.Latin1.GetBytes(entry.Folder + "\\" + entry.Name);
                prefix.WriteByte((byte)full.Length);
                prefix.Write(full);
            }

            var body = new MemoryStream();
            if (entry.Compressed)
            {
                body.Write(BitConverter.GetBytes((uint)entry.Data.Length));
                body.Write(version == 105 ? Lz4Frame(entry.Data) : ZLib(entry.Data));
            }
            else
            {
                body.Write(entry.Data);
            }

            var bytes = prefix.ToArray().Concat(body.ToArray()).ToArray();
            var sizeField = (uint)bytes.Length;
            if (entry.Compressed != defaultCompressed) sizeField |= 1u << 30;
            payloads.Add((bytes, sizeField));
        }

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(Encoding.ASCII.GetBytes("BSA\0"));
        w.Write(version);
        w.Write(36u);
        w.Write(archiveFlags);
        w.Write((uint)folders.Count);
        w.Write((uint)entries.Length);
        w.Write((uint)folders.Sum(f => f.Key.Length + 1));
        w.Write((uint)fileNames.Length);
        w.Write(0x3u);

        var blockPosition = 36 + folders.Count * folderRecordLength;
        foreach (var folder in folders)
        {
            w.Write(0x1111111111111111UL);
            w.Write((uint)folder.Count());
            if (longForm) w.Write(0u);
            // The offset in a folder record counts the total file name length as if it lead the block.
            var offset = (ulong)(blockPosition + nameBlockLength);
            if (longForm) w.Write(offset);
            else w.Write((uint)offset);
            blockPosition += (hasFolderNames ? 1 + folder.Key.Length + 1 : 0) + folder.Count() * 16;
        }

        var payloadIndex = 0;
        var dataPosition = dataStart;
        foreach (var folder in folders)
        {
            if (hasFolderNames)
            {
                w.Write((byte)(folder.Key.Length + 1));
                w.Write(Encoding.Latin1.GetBytes(folder.Key));
                w.Write((byte)0);
            }

            foreach (var _ in folder)
            {
                var (bytes, sizeField) = payloads[payloadIndex++];
                w.Write(0x2222222222222222UL);
                w.Write(sizeField);
                w.Write((uint)dataPosition);
                dataPosition += bytes.Length;
            }
        }

        if (hasFileNames) w.Write(fileNames.ToArray());
        foreach (var (bytes, _) in payloads) w.Write(bytes);
        return ms.ToArray();
    }

    public record Ba2Entry(string Name, byte[] Data, bool Compressed);

    /// <param name="Chunks">Each chunk's decompressed bytes and whether it is stored compressed.</param>
    public record Ba2Texture(string Name, ushort Width, ushort Height, byte Mips, byte Format, params (byte[] Data, bool Compressed)[] Chunks);

    /// <summary>
    /// Writes a BA2 from the layout described on the UESP wiki (https://en.uesp.net/wiki/Fallout4_Mod:Archive_File_Format
    /// for Fallout 4, with the Starfield header extension): a header, fixed size file records, the data, then a
    /// name table of 16 bit length prefixed names. <paramref name="compression"/> 3 stores chunks as raw LZ4
    /// blocks; version 3 is the only one that carries the field.
    /// </summary>
    public static byte[] Ba2General(uint version, uint compression, params Ba2Entry[] entries) =>
        Ba2("GNRL", version, compression, entries.Select(e => (e.Name, (object)e)).ToArray());

    public static byte[] Ba2Dx10(uint version, uint compression, params Ba2Texture[] textures) =>
        Ba2("DX10", version, compression, textures.Select(t => (t.Name, (object)t)).ToArray());

    private static byte[] Ba2(string type, uint version, uint compression, (string Name, object Entry)[] entries)
    {
        var headerLength = 24 + (version >= 2 ? 8 : 0) + (version == 3 ? 4 : 0);
        var recordsLength = entries.Sum(e => e.Entry is Ba2Texture t ? 24 + t.Chunks.Length * 24 : 36);

        // Encode every payload first so the records can carry absolute offsets.
        var payloads = new List<byte[]>();
        byte[] Encode(byte[] data, bool compressed)
        {
            if (!compressed) return data;
            if (version == 3 && compression == 3) return Lz4Block(data);
            return ZLib(data);
        }

        foreach (var (_, entry) in entries)
        {
            if (entry is Ba2Entry general) payloads.Add(Encode(general.Data, general.Compressed));
            else foreach (var chunk in ((Ba2Texture)entry).Chunks) payloads.Add(Encode(chunk.Data, chunk.Compressed));
        }

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        var dataStart = (ulong)(headerLength + recordsLength);
        var nameTableOffset = dataStart + (ulong)payloads.Sum(p => p.Length);

        w.Write(Encoding.ASCII.GetBytes("BTDX"));
        w.Write(version);
        w.Write(Encoding.ASCII.GetBytes(type));
        w.Write((uint)entries.Length);
        w.Write(nameTableOffset);
        if (version >= 2) { w.Write(1u); w.Write(2u); }
        if (version == 3) w.Write(compression);

        var offset = dataStart;
        var payloadIndex = 0;
        for (var i = 0; i < entries.Length; i++)
        {
            w.Write(0x10000000u + (uint)i); // name hash
            w.Write(Encoding.ASCII.GetBytes(entries[i].Entry is Ba2Texture ? "dds\0" : "txt\0"));
            w.Write(0x20000000u + (uint)i); // directory hash

            if (entries[i].Entry is Ba2Entry general)
            {
                var payload = payloads[payloadIndex++];
                w.Write(0x00100100u);
                w.Write(offset);
                w.Write(general.Compressed ? (uint)payload.Length : 0u);
                w.Write((uint)general.Data.Length);
                w.Write(0xBAADF00Du);
                offset += (ulong)payload.Length;
            }
            else
            {
                var texture = (Ba2Texture)entries[i].Entry;
                w.Write((byte)0);
                w.Write((byte)texture.Chunks.Length);
                w.Write((ushort)24);
                w.Write(texture.Height);
                w.Write(texture.Width);
                w.Write(texture.Mips);
                w.Write(texture.Format);
                w.Write((byte)0); // not a cube map
                w.Write((byte)8); // tile mode
                for (var c = 0; c < texture.Chunks.Length; c++)
                {
                    var payload = payloads[payloadIndex++];
                    w.Write(offset);
                    w.Write(texture.Chunks[c].Compressed ? (uint)payload.Length : 0u);
                    w.Write((uint)texture.Chunks[c].Data.Length);
                    w.Write((ushort)c); // start mip
                    w.Write((ushort)(c + 1)); // end mip
                    w.Write(0xBAADF00Du);
                    offset += (ulong)payload.Length;
                }
            }
        }

        foreach (var payload in payloads) w.Write(payload);
        foreach (var (name, _) in entries)
        {
            var bytes = Encoding.UTF8.GetBytes(name);
            w.Write((ushort)bytes.Length);
            w.Write(bytes);
        }

        return ms.ToArray();
    }

    public static byte[] Lz4Block(byte[] data)
    {
        var target = new byte[K4os.Compression.LZ4.LZ4Codec.MaximumOutputSize(data.Length)];
        var length = K4os.Compression.LZ4.LZ4Codec.Encode(data, target);
        return target.AsSpan(0, length).ToArray();
    }

    public static byte[] ZLib(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data);
        return ms.ToArray();
    }

    public static byte[] Lz4Frame(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var l = LZ4Stream.Encode(ms, leaveOpen: true)) l.Write(data);
        return ms.ToArray();
    }

    public static byte[] Pattern(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        // Runs make it compress, otherwise the compressed paths would just be stored blocks.
        for (var i = 0; i < length; i += 7) bytes[i] = 0x41;
        return bytes;
    }
}
