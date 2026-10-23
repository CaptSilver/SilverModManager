// Follows Wabbajack.Compression.BSA/BA2Archive/Reader.cs (GPL-3.0).
using System.Buffers.Binary;
using System.Text;
using NexusMods.Paths;

namespace NexusMods.Games.Bethesda.Archives;

internal sealed class Ba2ArchiveReader : IBethesdaArchiveReader
{
    public static ReadOnlySpan<byte> Magic => "BTDX"u8;

    private const int GeneralRecordLength = 36;
    private const int TextureRecordLength = 24;
    private const int ChunkRecordLength = 24;

    // The v3 compression field: 3 means LZ4 blocks, anything else zlib.
    private const uint LzCompression = 3;

    private readonly ArchiveSource _source;

    private Ba2ArchiveReader(ArchiveSource source, Ba2ArchiveState state, IReadOnlyList<IBethesdaArchiveEntry> entries)
    {
        _source = source;
        State = state;
        Entries = entries;
    }

    public ArchiveKind Kind => ArchiveKind.Ba2;

    public ArchiveState State { get; }

    public IReadOnlyList<IBethesdaArchiveEntry> Entries { get; }

    public ValueTask DisposeAsync() => _source.DisposeAsync();

    public static Ba2ArchiveReader Load(ArchiveSource source)
    {
        var version = source.ReadUInt32(4);
        var typeCode = Encoding.ASCII.GetString(source.ReadBytes(8, 4));
        var fileCount = source.ReadUInt32(12);
        var nameTableOffset = source.ReadUInt64(16);

        if (version is not (1 or 2 or 3 or 7 or 8))
            throw BethesdaArchiveException.Invalid(ArchiveKind.Ba2, 4, $"unsupported version {version}");

        var type = typeCode switch
        {
            "GNRL" => Ba2EntryType.Gnrl,
            "DX10" => Ba2EntryType.Dx10,
            "GNMF" => throw BethesdaArchiveException.Invalid(ArchiveKind.Ba2, 8, "GNMF (PlayStation 4) archives are not supported"),
            _ => throw BethesdaArchiveException.Invalid(ArchiveKind.Ba2, 8, $"unknown content type '{typeCode}'"),
        };

        long position = 24;
        uint unknown1 = 0, unknown2 = 0, compression = 0;
        if (version is 2 or 3)
        {
            unknown1 = source.ReadUInt32(position);
            unknown2 = source.ReadUInt32(position + 4);
            position += 8;
        }

        if (version == 3)
        {
            compression = source.ReadUInt32(position);
            position += 4;
        }

        var lz4 = compression == LzCompression;
        var records = type == Ba2EntryType.Gnrl
            ? ReadGeneral(source, position, fileCount)
            : ReadTextures(source, position, fileCount);

        var names = nameTableOffset > 0 ? ReadNames(source, nameTableOffset, fileCount) : null;

        var entries = new List<IBethesdaArchiveEntry>((int)Math.Min(fileCount, 1u << 20));
        for (var i = 0; i < fileCount; i++)
        {
            // Without a name table the only name an entry has is its name hash.
            var path = RelativePath.FromUnsanitizedInput(names?[i] ?? records[i].NameHash.ToString("X"));
            entries.Add(records[i].Build(source, i, path, lz4));
        }

        var state = new Ba2ArchiveState(nameTableOffset > 0, type, "BTDX", version, unknown1, unknown2, compression);
        return new Ba2ArchiveReader(source, state, entries);
    }

    private readonly record struct Record(uint NameHash, Func<ArchiveSource, int, RelativePath, bool, IBethesdaArchiveEntry> Build);

    private static Record[] ReadGeneral(ArchiveSource source, long position, uint fileCount)
    {
        source.RequireAvailable(position, (ulong)fileCount * GeneralRecordLength);
        var block = source.ReadBytes(position, checked((int)fileCount * GeneralRecordLength));
        var records = new Record[fileCount];
        for (var i = 0; i < records.Length; i++)
        {
            var r = block.AsSpan(i * GeneralRecordLength, GeneralRecordLength);
            var nameHash = BinaryPrimitives.ReadUInt32LittleEndian(r);
            var extension = Encoding.UTF8.GetString(r.Slice(4, 4));
            var dirHash = BinaryPrimitives.ReadUInt32LittleEndian(r[8..]);
            var flags = BinaryPrimitives.ReadUInt32LittleEndian(r[12..]);
            var offset = BinaryPrimitives.ReadUInt64LittleEndian(r[16..]);
            var packedSize = BinaryPrimitives.ReadUInt32LittleEndian(r[24..]);
            var fullSize = BinaryPrimitives.ReadUInt32LittleEndian(r[28..]);
            var align = BinaryPrimitives.ReadUInt32LittleEndian(r[32..]);

            if (offset > long.MaxValue)
                throw BethesdaArchiveException.Invalid(ArchiveKind.Ba2, position + i * GeneralRecordLength + 16, "file offset is out of range");

            records[i] = new Record(nameHash, (src, index, path, lz4) => new Ba2GeneralEntry(
                src,
                new Ba2FileState(index, path, extension, packedSize != 0, align, flags, dirHash, nameHash),
                (long)offset, packedSize, fullSize, lz4));
        }

        return records;
    }

    private static Record[] ReadTextures(ArchiveSource source, long position, uint fileCount)
    {
        // Texture records vary in length, but none is shorter than its fixed part.
        source.RequireAvailable(position, (ulong)fileCount * TextureRecordLength);
        var records = new Record[fileCount];
        for (var i = 0; i < records.Length; i++)
        {
            var head = source.ReadBytes(position, TextureRecordLength);
            var nameHash = BinaryPrimitives.ReadUInt32LittleEndian(head);
            var extension = Encoding.UTF8.GetString(head.AsSpan(4, 4));
            var dirHash = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(8));
            var unk8 = head[12];
            var chunkCount = head[13];
            var chunkHeaderLength = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(14));
            var height = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(16));
            var width = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(18));
            var mips = head[20];
            var format = head[21];
            var cubeMap = head[22];
            var tileMode = head[23];
            position += TextureRecordLength;

            var chunkBytes = source.ReadBytes(position, chunkCount * ChunkRecordLength);
            position += chunkBytes.Length;
            var chunks = new Ba2Chunk[chunkCount];
            var locations = new Ba2TextureEntry.ChunkLocation[chunkCount];
            for (var c = 0; c < chunkCount; c++)
            {
                var r = chunkBytes.AsSpan(c * ChunkRecordLength, ChunkRecordLength);
                var offset = BinaryPrimitives.ReadUInt64LittleEndian(r);
                if (offset > long.MaxValue)
                    throw BethesdaArchiveException.Invalid(ArchiveKind.Ba2, position - chunkBytes.Length + c * ChunkRecordLength, "chunk offset is out of range");

                var packedSize = BinaryPrimitives.ReadUInt32LittleEndian(r[8..]);
                chunks[c] = new Ba2Chunk(
                    Compressed: packedSize != 0,
                    Align: BinaryPrimitives.ReadUInt32LittleEndian(r[20..]),
                    EndMip: BinaryPrimitives.ReadUInt16LittleEndian(r[18..]),
                    StartMip: BinaryPrimitives.ReadUInt16LittleEndian(r[16..]),
                    FullSz: BinaryPrimitives.ReadUInt32LittleEndian(r[12..]));
                locations[c] = new Ba2TextureEntry.ChunkLocation((long)offset, packedSize);
            }

            records[i] = new Record(nameHash, (src, index, path, lz4) => new Ba2TextureEntry(
                src,
                new Ba2Dx10FileState(index, path, chunks, format, mips, width, height, chunkHeaderLength, unk8, dirHash, extension, nameHash, cubeMap, tileMode),
                locations, lz4));
        }

        return records;
    }

    private static string[] ReadNames(ArchiveSource source, ulong tableOffset, uint fileCount)
    {
        if (tableOffset > (ulong)source.Length)
            throw BethesdaArchiveException.Truncated(ArchiveKind.Ba2, (long)Math.Min(tableOffset, long.MaxValue), (long)fileCount * 2);

        var offset = (long)tableOffset;
        var table = source.ReadBytes(offset, checked((int)(source.Length - offset)));
        var names = new string[fileCount];
        var cursor = 0;
        for (var i = 0; i < names.Length; i++)
        {
            if (cursor + 2 > table.Length)
                throw BethesdaArchiveException.Truncated(ArchiveKind.Ba2, offset + cursor, 2);
            var length = BinaryPrimitives.ReadUInt16LittleEndian(table.AsSpan(cursor));
            cursor += 2;
            if (cursor + length > table.Length)
                throw BethesdaArchiveException.Truncated(ArchiveKind.Ba2, offset + cursor, length);
            names[i] = Encoding.UTF8.GetString(table, cursor, length);
            cursor += length;
        }

        return names;
    }
}
