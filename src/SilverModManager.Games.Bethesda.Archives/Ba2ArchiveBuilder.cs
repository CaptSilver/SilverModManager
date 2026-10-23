// Follows Wabbajack.Compression.BSA/BA2Archive/Builder.cs, FileEntryBuilder.cs, DX10FileEntryBuilder.cs and ChunkBuilder.cs (GPL-3.0).
using System.Text;
using ICSharpCode.SharpZipLib.Zip.Compression;
using NexusMods.Paths;

namespace NexusMods.Games.Bethesda.Archives;

internal sealed class Ba2ArchiveBuilder : IBethesdaArchiveBuilder
{
    private const int GeneralRecordLength = 36;
    private const int TextureRecordLength = 24;
    private const int ChunkRecordLength = 24;

    // The v3 compression field: 3 means LZ4 blocks, anything else zlib.
    private const uint LzCompression = 3;

    private readonly Ba2ArchiveState _state;
    private readonly TemporaryFileManager _temporaryFileManager;
    private readonly bool _lz4;
    private readonly List<Member> _members = [];

    /// <param name="Packed">For each stored run (one for a general file, one per chunk for a texture), its
    /// length once compressed; zero for a run kept as it is.</param>
    private sealed record Member(FileState State, StagedData Data, uint[] Packed, uint FullSize);

    public Ba2ArchiveBuilder(Ba2ArchiveState state, TemporaryFileManager temporaryFileManager)
    {
        _state = state;
        _temporaryFileManager = temporaryFileManager;
        if (state.Version is not (1 or 2 or 3 or 7 or 8))
            throw new BethesdaArchiveException($"Cannot build a Ba2 archive of version {state.Version}");
        if (state.Type is not (Ba2EntryType.Gnrl or Ba2EntryType.Dx10))
            throw new BethesdaArchiveException($"Cannot build a Ba2 archive of type {state.Type}");

        _lz4 = state.Compression == LzCompression;
    }

    public async Task AddFile(FileState state, Stream data, CancellationToken cancellationToken)
    {
        Member member;
        switch (state, _state.Type)
        {
            case (Ba2FileState general, Ba2EntryType.Gnrl):
                member = await StageGeneral(general, data, cancellationToken);
                break;
            case (Ba2Dx10FileState texture, Ba2EntryType.Dx10):
                member = await StageTexture(texture, data, cancellationToken);
                break;
            default:
                throw new BethesdaArchiveException($"A Ba2 {_state.Type} archive cannot take a {state.GetType().Name}");
        }

        lock (_members)
        {
            _members.Add(member);
        }
    }

    private async Task<Member> StageGeneral(Ba2FileState state, Stream data, CancellationToken cancellationToken)
    {
        CheckExtension(state.Extension, state.Path);

        long fullSize = 0;
        long packedSize = 0;
        var staged = await StagedData.Create(_temporaryFileManager, async output =>
        {
            if (!state.Compressed)
            {
                fullSize = await ArchiveCompression.CopyCounting(data, output, cancellationToken);
            }
            else if (_lz4)
            {
                // The reader decodes a general file as LZ4 whenever the archive says so, and so does this.
                var full = new MemoryStream();
                await data.CopyToAsync(full, cancellationToken);
                fullSize = full.Length;
                await output.WriteAsync(ArchiveCompression.Lz4Block(full.ToArray()), cancellationToken);
            }
            else
            {
                fullSize = await ArchiveCompression.Deflate(data, output, Deflater.DEFAULT_COMPRESSION, cancellationToken);
            }

            packedSize = state.Compressed ? output.Length : 0;
        });

        if (fullSize > uint.MaxValue || packedSize > uint.MaxValue)
        {
            await staged.DisposeAsync();
            throw new BethesdaArchiveException($"{state.Path} is too large for a Ba2 archive");
        }

        return new Member(state, staged, [(uint)packedSize], (uint)fullSize);
    }

    private async Task<Member> StageTexture(Ba2Dx10FileState state, Stream data, CancellationToken cancellationToken)
    {
        CheckExtension(state.Extension, state.Path);
        if (state.Chunks.Length > byte.MaxValue)
            throw new BethesdaArchiveException($"{state.Path} has {state.Chunks.Length} chunks, more than a Ba2 record can count");

        // The archive keeps only mip data; the DDS header the source starts with is rebuilt on read.
        var headerLength = DdsHeader.Build(state.Width, state.Height, state.NumMips, state.PixelFormat, state.IsCubeMap == 1).Length;
        var header = await ArchiveCompression.ReadExactly(data, headerLength, $"The DDS of {state.Path}", cancellationToken);
        if (!header.AsSpan(0, 4).SequenceEqual("DDS "u8))
            throw new BethesdaArchiveException($"{state.Path} does not start with a DDS header");

        var packed = new uint[state.Chunks.Length];
        var staged = await StagedData.Create(_temporaryFileManager, async output =>
        {
            for (var i = 0; i < state.Chunks.Length; i++)
            {
                var chunk = state.Chunks[i];
                if (chunk.FullSz > int.MaxValue)
                    throw new BethesdaArchiveException($"{state.Path} has a chunk of {chunk.FullSz} bytes, too large for a Ba2 archive");

                var full = await ArchiveCompression.ReadExactly(
                    data, (int)chunk.FullSz, $"The mip data of {state.Path}", cancellationToken);
                if (!chunk.Compressed)
                {
                    await output.WriteAsync(full, cancellationToken);
                    continue;
                }

                var before = output.Length;
                if (_lz4)
                    await output.WriteAsync(ArchiveCompression.Lz4Block(full), cancellationToken);
                else
                    await ArchiveCompression.Deflate(new MemoryStream(full), output, Deflater.BEST_COMPRESSION, cancellationToken);
                packed[i] = checked((uint)(output.Length - before));
            }
        });

        return new Member(state, staged, packed, 0);
    }

    public async Task Build(Stream output, CancellationToken cancellationToken)
    {
        Member[] members;
        lock (_members) members = [.. _members.OrderBy(m => m.State.Index)];
        for (var i = 1; i < members.Length; i++)
        {
            if (members[i].State.Index == members[i - 1].State.Index)
                throw new BethesdaArchiveException($"Two files have index {members[i].State.Index} in a Ba2 archive");
        }

        var headerLength = 24 + (_state.Version is 2 or 3 ? 8 : 0) + (_state.Version == 3 ? 4 : 0);
        var recordsLength = members.Sum(m => m.State is Ba2Dx10FileState t
            ? TextureRecordLength + (long)t.Chunks.Length * ChunkRecordLength
            : GeneralRecordLength);
        var dataStart = headerLength + recordsLength;
        var dataLength = members.Sum(m => m.Data.Length);

        var header = new MemoryStream();
        using (var w = new BinaryWriter(header, Encoding.ASCII, leaveOpen: true))
        {
            w.Write("BTDX"u8);
            w.Write(_state.Version);
            w.Write(Encoding.ASCII.GetBytes(_state.Type == Ba2EntryType.Gnrl ? "GNRL" : "DX10"));
            w.Write((uint)members.Length);
            w.Write(_state.HasNameTable ? (ulong)(dataStart + dataLength) : 0UL);
            if (_state.Version is 2 or 3)
            {
                w.Write(_state.Unknown1);
                w.Write(_state.Unknown2);
                if (_state.Version == 3) w.Write(_state.Compression);
            }

            var offset = (ulong)dataStart;
            foreach (var member in members)
            {
                switch (member.State)
                {
                    case Ba2FileState general:
                        w.Write(general.NameHash);
                        w.Write(Encoding.UTF8.GetBytes(general.Extension));
                        w.Write(general.DirHash);
                        w.Write(general.Flags);
                        w.Write(offset);
                        w.Write(member.Packed[0]);
                        w.Write(member.FullSize);
                        w.Write(general.Align);
                        offset += (ulong)member.Data.Length;
                        break;
                    case Ba2Dx10FileState texture:
                        w.Write(texture.NameHash);
                        w.Write(Encoding.UTF8.GetBytes(texture.Extension));
                        w.Write(texture.DirHash);
                        w.Write(texture.Unk8);
                        w.Write((byte)texture.Chunks.Length);
                        w.Write(texture.ChunkHdrLen);
                        w.Write(texture.Height);
                        w.Write(texture.Width);
                        w.Write(texture.NumMips);
                        w.Write(texture.PixelFormat);
                        w.Write(texture.IsCubeMap);
                        w.Write(texture.TileMode);
                        for (var i = 0; i < texture.Chunks.Length; i++)
                        {
                            var chunk = texture.Chunks[i];
                            w.Write(offset);
                            w.Write(member.Packed[i]);
                            w.Write(chunk.FullSz);
                            w.Write(chunk.StartMip);
                            w.Write(chunk.EndMip);
                            w.Write(chunk.Align);
                            offset += chunk.Compressed ? member.Packed[i] : chunk.FullSz;
                        }

                        break;
                }
            }
        }

        await output.WriteAsync(header.GetBuffer().AsMemory(0, (int)header.Length), cancellationToken);
        foreach (var member in members) await member.Data.CopyTo(output, cancellationToken);

        if (!_state.HasNameTable) return;
        var names = new MemoryStream();
        using (var w = new BinaryWriter(names, Encoding.UTF8, leaveOpen: true))
        {
            foreach (var member in members)
            {
                var bytes = Encoding.UTF8.GetBytes(member.State.Path.ToString().Replace('/', '\\'));
                if (bytes.Length > ushort.MaxValue)
                    throw new BethesdaArchiveException($"The path '{member.State.Path}' is too long for a Ba2 name table");
                w.Write((ushort)bytes.Length);
                w.Write(bytes);
            }
        }

        await output.WriteAsync(names.GetBuffer().AsMemory(0, (int)names.Length), cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        Member[] members;
        lock (_members) members = [.. _members];
        foreach (var member in members) await member.Data.DisposeAsync();
    }

    private static void CheckExtension(string extension, RelativePath path)
    {
        if (Encoding.UTF8.GetByteCount(extension) != 4)
            throw new BethesdaArchiveException($"{path} records the extension '{extension}', which is not the four bytes a Ba2 record holds");
    }
}
