// Follows Wabbajack.Compression.BSA/TES5Archive/Reader.cs, FolderRecord.cs and FileNameBlock.cs (GPL-3.0).
using System.Buffers.Binary;
using System.Text;
using NexusMods.Paths;

namespace SilverModManager.Games.Bethesda.Archives;

internal sealed class Tes5ArchiveReader : IBethesdaArchiveReader
{
    public static ReadOnlySpan<byte> Magic => "BSA\0"u8;

    private const int HeaderLength = 36;
    private const int FileRecordLength = 16;

    // Archive flags.
    private const uint HasFolderNames = 0x1;
    private const uint HasFileNames = 0x2;
    private const uint CompressedByDefault = 0x4;
    private const uint EmbeddedFileNames = 0x100;

    private readonly ArchiveSource _source;

    private Tes5ArchiveReader(ArchiveSource source, Tes5ArchiveState state, IReadOnlyList<IBethesdaArchiveEntry> entries)
    {
        _source = source;
        State = state;
        Entries = entries;
    }

    public ArchiveKind Kind => ArchiveKind.Tes5;

    public ArchiveState State { get; }

    public IReadOnlyList<IBethesdaArchiveEntry> Entries { get; }

    public ValueTask DisposeAsync() => _source.DisposeAsync();

    public static Tes5ArchiveReader Load(ArchiveSource source)
    {
        // Field by field, so a short stream reports the offset of the field it lacks.
        var version = source.ReadUInt32(4);
        var folderRecordOffset = source.ReadUInt32(8);
        var archiveFlags = source.ReadUInt32(12);
        var folderCount = source.ReadUInt32(16);
        var fileCount = source.ReadUInt32(20);
        source.ReadUInt32(24); // total folder name length: the names carry their own lengths
        var totalFileNameLength = source.ReadUInt32(28);
        var fileFlags = source.ReadUInt32(32);

        if (version is not (103 or 104 or 105))
            throw BethesdaArchiveException.Invalid(ArchiveKind.Tes5, 4, $"unsupported version {version}");

        var encoding = ArchiveEncodings.ForTes5Version(version);
        var folderRecordLength = version == 105 ? 24 : 16;
        var embeddedNames = version >= 104 && (archiveFlags & EmbeddedFileNames) != 0;
        var hasFolderNames = (archiveFlags & HasFolderNames) != 0;

        if ((archiveFlags & HasFileNames) == 0)
            throw BethesdaArchiveException.Invalid(ArchiveKind.Tes5, 12, "archive has no file names");

        source.RequireAvailable(folderRecordOffset, (ulong)folderCount * (ulong)folderRecordLength);
        var folderRecords = source.ReadBytes(folderRecordOffset, checked((int)folderCount * folderRecordLength));

        // The file record blocks follow the folder records back to back, whatever the folder offsets say.
        var position = folderRecordOffset + (long)folderCount * folderRecordLength;
        var folders = new List<(string Name, byte[] Records)>((int)Math.Min(folderCount, 4096));
        ulong filesSeen = 0;
        for (var i = 0; i < folderCount; i++)
        {
            var count = BinaryPrimitives.ReadUInt32LittleEndian(folderRecords.AsSpan(i * folderRecordLength + 8));
            filesSeen += count;
            if (filesSeen > fileCount)
                throw BethesdaArchiveException.Invalid(ArchiveKind.Tes5, folderRecordOffset + (long)i * folderRecordLength, "folders hold more files than the header counts");

            var name = string.Empty;
            if (hasFolderNames)
            {
                var length = source.ReadByte(position);
                var raw = source.ReadBytes(position + 1, length);
                // The stored length counts the terminating zero.
                name = length == 0 ? string.Empty : encoding.GetString(raw.AsSpan(0, length - 1));
                position += 1 + length;
            }

            var records = source.ReadBytes(position, checked((int)count * FileRecordLength));
            position += records.Length;
            folders.Add((name, records));
        }

        if (filesSeen != fileCount)
            throw BethesdaArchiveException.Invalid(ArchiveKind.Tes5, 20, "folders hold fewer files than the header counts");

        var names = ReadFileNames(source, position, totalFileNameLength, fileCount, encoding);
        var defaultCompressed = (archiveFlags & CompressedByDefault) != 0;

        var entries = new List<IBethesdaArchiveEntry>((int)fileCount);
        foreach (var (folderName, records) in folders)
        {
            for (var index = 0; index < records.Length / FileRecordLength; index++)
            {
                var record = records.AsSpan(index * FileRecordLength, FileRecordLength);
                var rawSize = BinaryPrimitives.ReadUInt32LittleEndian(record[8..]);
                var offset = BinaryPrimitives.ReadUInt32LittleEndian(record[12..]);
                var name = names[entries.Count];

                var path = RelativePath.FromUnsanitizedInput(folderName.Length == 0 ? name : folderName + "\\" + name);
                var flip = (rawSize & (1u << 30)) != 0;
                var state = new Tes5FileState(index, path, flip);
                entries.Add(Tes5ArchiveEntry.Create(
                    source, state, offset, flip ? rawSize ^ (1u << 30) : rawSize,
                    compressed: flip != defaultCompressed, embeddedNames, lz4: version == 105));
            }
        }

        var archiveState = new Tes5ArchiveState("BSA\0", version, archiveFlags, fileFlags);
        return new Tes5ArchiveReader(source, archiveState, entries);
    }

    private static string[] ReadFileNames(ArchiveSource source, long position, uint totalLength, uint fileCount, Encoding encoding)
    {
        source.RequireAvailable(position, totalLength);
        var block = source.ReadBytes(position, checked((int)totalLength));
        var names = new string[fileCount];
        var cursor = 0;
        for (var i = 0; i < names.Length; i++)
        {
            var end = block.AsSpan(cursor).IndexOf((byte)0);
            if (end < 0)
                throw BethesdaArchiveException.Invalid(ArchiveKind.Tes5, position + cursor, "file name is not terminated");
            names[i] = encoding.GetString(block, cursor, end);
            cursor += end + 1;
        }

        // Official archives pad the block with zeros, so leftover bytes are fine.
        return names;
    }
}
