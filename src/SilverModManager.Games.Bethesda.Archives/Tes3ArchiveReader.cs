// Follows Wabbajack.Compression.BSA/TES3Archive/Reader.cs and FileEntry.cs (GPL-3.0).
using NexusMods.Paths;

namespace NexusMods.Games.Bethesda.Archives;

internal sealed class Tes3ArchiveReader : IBethesdaArchiveReader
{
    public static ReadOnlySpan<byte> Magic => [0, 1, 0, 0];

    private const int HeaderLength = 12;

    private readonly ArchiveSource _source;

    private Tes3ArchiveReader(ArchiveSource source, Tes3ArchiveState state, IReadOnlyList<IBethesdaArchiveEntry> entries)
    {
        _source = source;
        State = state;
        Entries = entries;
    }

    public ArchiveKind Kind => ArchiveKind.Tes3;

    public ArchiveState State { get; }

    public IReadOnlyList<IBethesdaArchiveEntry> Entries { get; }

    public ValueTask DisposeAsync() => _source.DisposeAsync();

    public static Tes3ArchiveReader Load(ArchiveSource source)
    {
        var version = source.ReadUInt32(0);
        var hashOffset = source.ReadUInt32(4);
        var fileCount = source.ReadUInt32(8);

        // Per file: size and offset (8), name offset (4). The hash table is 8 bytes per file.
        source.RequireAvailable(HeaderLength, (ulong)fileCount * 12);

        var table = source.ReadBytes(HeaderLength, checked((int)fileCount * 8));
        var nameOffsetsStart = HeaderLength + (long)fileCount * 8;
        var nameOffsets = source.ReadBytes(nameOffsetsStart, checked((int)fileCount * 4));

        // Names sit between the name offsets and the hash table, which hashOffset (counted from the end of
        // the header) points at.
        var namesStart = nameOffsetsStart + (long)fileCount * 4;
        var hashTableStart = HeaderLength + (long)hashOffset;
        if (hashTableStart < namesStart)
            throw BethesdaArchiveException.Invalid(ArchiveKind.Tes3, 4, $"hash table offset {hashOffset} lies inside the file table");
        source.RequireAvailable(namesStart, (ulong)(hashTableStart - namesStart) + (ulong)fileCount * 8);
        var names = source.ReadBytes(namesStart, checked((int)(hashTableStart - namesStart)));
        var hashes = source.ReadBytes(hashTableStart, checked((int)fileCount * 8));
        var dataOffset = hashTableStart + (long)fileCount * 8;

        var entries = new IBethesdaArchiveEntry[fileCount];
        for (var i = 0; i < entries.Length; i++)
        {
            var size = BitConverter.ToUInt32(table, i * 8);
            var offset = BitConverter.ToUInt32(table, i * 8 + 4);
            var nameOffset = BitConverter.ToUInt32(nameOffsets, i * 4);
            var path = ReadName(names, nameOffset, namesStart);

            var state = new Tes3FileState(
                i, path, offset, nameOffset,
                BitConverter.ToUInt32(hashes, i * 8), BitConverter.ToUInt32(hashes, i * 8 + 4), size);
            entries[i] = new Tes3ArchiveEntry(source, state, dataOffset + offset);
        }

        var archiveState = new Tes3ArchiveState(fileCount, dataOffset, hashOffset, version);
        return new Tes3ArchiveReader(source, archiveState, entries);
    }

    private static RelativePath ReadName(byte[] names, uint nameOffset, long namesStart)
    {
        if (nameOffset >= names.Length)
            throw BethesdaArchiveException.Invalid(ArchiveKind.Tes3, namesStart + nameOffset, "name offset is past the name block");
        var span = names.AsSpan((int)nameOffset);
        var end = span.IndexOf((byte)0);
        if (end < 0)
            throw BethesdaArchiveException.Invalid(ArchiveKind.Tes3, namesStart + nameOffset, "name is not terminated");
        return RelativePath.FromUnsanitizedInput(ArchiveEncodings.Ascii.GetString(span[..end]));
    }

    private sealed class Tes3ArchiveEntry : IBethesdaArchiveEntry
    {
        private readonly ArchiveSource _source;
        private readonly Tes3FileState _state;
        private readonly long _position;

        public Tes3ArchiveEntry(ArchiveSource source, Tes3FileState state, long position)
        {
            _source = source;
            _state = state;
            _position = position;
        }

        public RelativePath Path => _state.Path;

        public ulong Size => _state.Size;

        public FileState State => _state;

        public async Task CopyToAsync(Stream destination, CancellationToken cancellationToken)
        {
            using var _ = await _source.Lock(cancellationToken);
            _source.RequireAvailable(_position, _state.Size);
            _source.Stream.Position = _position;
            await ArchiveCopy.CopyExactly(
                new BoundedReadStream(_source, _state.Size), destination, _state.Size,
                ArchiveKind.Tes3, Path.ToString(), cancellationToken);
        }
    }
}
