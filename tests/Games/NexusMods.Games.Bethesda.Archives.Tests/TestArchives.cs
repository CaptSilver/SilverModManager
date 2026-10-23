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
