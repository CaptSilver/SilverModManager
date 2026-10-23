// Follows Wabbajack.Compression.BSA/TES5Archive/Builder.cs (GPL-3.0).
using System.Buffers.Binary;
using System.Text;
using ICSharpCode.SharpZipLib.Zip.Compression;
using NexusMods.Paths;

namespace SilverModManager.Games.Bethesda.Archives;

internal sealed class Tes5ArchiveBuilder : IBethesdaArchiveBuilder
{
    private const int HeaderLength = 36;
    private const int FileRecordLength = 16;

    private const uint HasFolderNames = 0x1;
    private const uint HasFileNames = 0x2;
    private const uint CompressedByDefault = 0x4;
    private const uint EmbeddedFileNames = 0x100;

    private const uint FlipCompressionFlag = 1u << 30;

    private readonly Tes5ArchiveState _state;
    private readonly TemporaryFileManager _temporaryFileManager;
    private readonly Encoding _encoding;
    private readonly bool _embedNames;
    private readonly List<Member> _members = [];

    /// <param name="Folder">The folder as written to the archive, backslash separated; empty for the root.</param>
    /// <param name="OriginalSize">The size before compression; only stored when the entry is compressed.</param>
    private sealed record Member(
        Tes5FileState State, string Folder, string Name, StagedData Data, uint OriginalSize, bool Compressed);

    public Tes5ArchiveBuilder(Tes5ArchiveState state, TemporaryFileManager temporaryFileManager)
    {
        _state = state;
        _temporaryFileManager = temporaryFileManager;
        if (state.Version is not (103 or 104 or 105))
            throw new BethesdaArchiveException($"Cannot build a Tes5 archive of version {state.Version}");
        if ((state.ArchiveFlags & HasFileNames) == 0)
            throw new BethesdaArchiveException("Cannot build a Tes5 archive without file names: the reader refuses them");

        _encoding = ArchiveEncodings.ForTes5Version(state.Version);
        _embedNames = state.Version >= 104 && (state.ArchiveFlags & EmbeddedFileNames) != 0;
    }

    private bool HasFolderNamesFlag => (_state.ArchiveFlags & HasFolderNames) != 0;

    public async Task AddFile(FileState state, Stream data, CancellationToken cancellationToken)
    {
        if (state is not Tes5FileState file)
            throw new BethesdaArchiveException($"A Tes5 archive cannot take a {state.GetType().Name}");

        var path = file.Path.ToString().Replace('/', '\\');
        var slash = path.LastIndexOf('\\');
        var folder = slash < 0 ? string.Empty : path[..slash];
        var name = path[(slash + 1)..];
        if (name.Length == 0)
            throw new BethesdaArchiveException("A Tes5 archive member needs a file name");

        var compressed = file.FlipCompression != ((_state.ArchiveFlags & CompressedByDefault) != 0);
        long originalSize = 0;
        var staged = await StagedData.Create(_temporaryFileManager, async output =>
        {
            if (!compressed)
                originalSize = await ArchiveCompression.CopyCounting(data, output, cancellationToken);
            else if (_state.Version == 105)
                originalSize = await ArchiveCompression.Lz4Frame(data, output, cancellationToken);
            else
                originalSize = await ArchiveCompression.Deflate(data, output, Deflater.DEFAULT_COMPRESSION, cancellationToken);
        });

        if (originalSize >= FlipCompressionFlag)
        {
            await staged.DisposeAsync();
            throw new BethesdaArchiveException($"{file.Path} is too large for a Tes5 archive");
        }

        lock (_members)
        {
            _members.Add(new Member(file, folder, name, staged, (uint)originalSize, compressed));
        }
    }

    public async Task Build(Stream output, CancellationToken cancellationToken)
    {
        Member[] members;
        lock (_members) members = [.. _members];

        // Folders and the files in them are ordered by hash, which is how the games look entries up. Equal
        // hashes fall back to the recorded index and then the name, so a build does not depend on the order
        // the files were added in.
        var folders = members
            .GroupBy(m => m.Folder, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var files = g
                    .OrderBy(m => Tes5Hashes.File(m.Name))
                    .ThenBy(m => m.State.Index)
                    .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                return (Name: g.Key, Hash: Tes5Hashes.Folder(g.Key), Files: files);
            })
            .OrderBy(f => f.Hash)
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var folderRecordLength = _state.Version == 105 ? 24 : 16;
        var folderNames = folders.Select(f => HasFolderNamesFlag ? BZString(f.Name) : []).ToArray();
        var fileNames = folders.SelectMany(f => f.Files).Select(m => NulTerminated(m.Name)).ToArray();
        var totalFileNameLength = fileNames.Sum(n => n.Length);

        // The block of file records and folder names for each folder comes after all the folder records.
        long blocksStart = HeaderLength + (long)folders.Length * folderRecordLength;
        var blockLengths = folders.Select((f, i) => folderNames[i].Length + f.Files.Length * FileRecordLength).ToArray();
        var dataStart = blocksStart + blockLengths.Sum(l => (long)l) + totalFileNameLength;

        // Lay the data out first: each record carries its payload's absolute offset.
        var prefixes = new Dictionary<Member, byte[]>(ReferenceEqualityComparer.Instance);
        var offsets = new Dictionary<Member, long>(ReferenceEqualityComparer.Instance);
        var position = dataStart;
        foreach (var member in folders.SelectMany(f => f.Files))
        {
            var prefix = EmbeddedName(member);
            prefixes[member] = prefix;
            offsets[member] = position;
            var length = prefix.Length + (member.Compressed ? 4 : 0) + member.Data.Length;
            if (length >= FlipCompressionFlag)
                throw new BethesdaArchiveException($"{member.State.Path} is too large for a Tes5 archive");
            position += length;
            if (position > uint.MaxValue)
                throw new BethesdaArchiveException("A Tes5 archive cannot hold more than 4 GiB");
        }

        var header = new MemoryStream();
        using (var w = new BinaryWriter(header, Encoding.ASCII, leaveOpen: true))
        {
            w.Write("BSA\0"u8);
            w.Write(_state.Version);
            w.Write((uint)HeaderLength);
            w.Write(_state.ArchiveFlags);
            w.Write((uint)folders.Length);
            w.Write((uint)members.Length);
            w.Write((uint)folderNames.Sum(n => n.Length == 0 ? 0 : n.Length - 1));
            w.Write((uint)totalFileNameLength);
            w.Write(_state.FileFlags);

            // A folder record's offset counts the file name block as if it came before the folder's block.
            long blockPosition = blocksStart;
            for (var i = 0; i < folders.Length; i++)
            {
                w.Write(folders[i].Hash);
                w.Write((uint)folders[i].Files.Length);
                var offset = blockPosition + totalFileNameLength;
                if (_state.Version == 105)
                {
                    w.Write(0u);
                    w.Write((ulong)offset);
                }
                else
                {
                    w.Write(checked((uint)offset));
                }

                blockPosition += blockLengths[i];
            }

            for (var i = 0; i < folders.Length; i++)
            {
                w.Write(folderNames[i]);
                foreach (var member in folders[i].Files)
                {
                    var length = (uint)(prefixes[member].Length + (member.Compressed ? 4 : 0) + member.Data.Length);
                    w.Write(Tes5Hashes.File(member.Name));
                    w.Write(member.State.FlipCompression ? length | FlipCompressionFlag : length);
                    w.Write((uint)offsets[member]);
                }
            }

            foreach (var name in fileNames) w.Write(name);
        }

        await output.WriteAsync(header.GetBuffer().AsMemory(0, (int)header.Length), cancellationToken);

        foreach (var member in folders.SelectMany(f => f.Files))
        {
            await output.WriteAsync(prefixes[member], cancellationToken);
            if (member.Compressed)
            {
                var size = new byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(size, member.OriginalSize);
                await output.WriteAsync(size, cancellationToken);
            }

            await member.Data.CopyTo(output, cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Member[] members;
        lock (_members) members = [.. _members];
        foreach (var member in members) await member.Data.DisposeAsync();
    }

    private byte[] NulTerminated(string text) => [.. _encoding.GetBytes(text), 0];

    /// <summary>A length prefixed, zero terminated string; the length counts the terminator.</summary>
    private byte[] BZString(string text)
    {
        var bytes = _encoding.GetBytes(text);
        if (bytes.Length + 1 > byte.MaxValue)
            throw new BethesdaArchiveException($"The folder name '{text}' is too long for a Tes5 archive");
        return [(byte)(bytes.Length + 1), .. bytes, 0];
    }

    /// <summary>The path in front of the data, for archives that embed names; a length prefixed string without a terminator.</summary>
    private byte[] EmbeddedName(Member member)
    {
        if (!_embedNames) return [];
        var bytes = _encoding.GetBytes(member.Folder.Length == 0 ? member.Name : member.Folder + "\\" + member.Name);
        if (bytes.Length > byte.MaxValue)
            throw new BethesdaArchiveException($"The path '{member.State.Path}' is too long to embed in a Tes5 archive");
        return [(byte)bytes.Length, .. bytes];
    }
}
