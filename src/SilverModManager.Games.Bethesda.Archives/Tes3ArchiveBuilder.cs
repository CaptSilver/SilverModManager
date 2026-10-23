// Follows Wabbajack.Compression.BSA/TES3Archive/Builder.cs (GPL-3.0).
using NexusMods.Paths;

namespace NexusMods.Games.Bethesda.Archives;

internal sealed class Tes3ArchiveBuilder : IBethesdaArchiveBuilder
{
    private const int HeaderLength = 12;

    private readonly Tes3ArchiveState _state;
    private readonly TemporaryFileManager _temporaryFileManager;
    private readonly StagedData?[] _data;
    private readonly Tes3FileState?[] _files;

    public Tes3ArchiveBuilder(Tes3ArchiveState state, TemporaryFileManager temporaryFileManager)
    {
        _state = state;
        _temporaryFileManager = temporaryFileManager;
        // Sized by the header's count, so an absurd count fails here and not in the middle of a build.
        if (state.FileCount > 1u << 24)
            throw new BethesdaArchiveException($"A Tes3 archive state cannot hold {state.FileCount} files");
        _data = new StagedData?[state.FileCount];
        _files = new Tes3FileState?[state.FileCount];
    }

    public async Task AddFile(FileState state, Stream data, CancellationToken cancellationToken)
    {
        if (state is not Tes3FileState file)
            throw new BethesdaArchiveException($"A Tes3 archive cannot take a {state.GetType().Name}");
        if (file.Index < 0 || file.Index >= _files.Length)
            throw new BethesdaArchiveException($"{file.Path} has index {file.Index}, outside the archive's {_files.Length} file(s)");

        var staged = await StagedData.Create(_temporaryFileManager, output => data.CopyToAsync(output, cancellationToken));
        if ((ulong)staged.Length != file.Size)
        {
            await staged.DisposeAsync();
            throw new BethesdaArchiveException($"{file.Path} has {staged.Length} byte(s) but its state records {file.Size}");
        }

        lock (_files)
        {
            if (_files[file.Index] is null)
            {
                _files[file.Index] = file;
                _data[file.Index] = staged;
                return;
            }
        }

        await staged.DisposeAsync();
        throw new BethesdaArchiveException($"Two files have index {file.Index} in a Tes3 archive");
    }

    public async Task Build(Stream output, CancellationToken cancellationToken)
    {
        var files = new Tes3FileState[_files.Length];
        var data = new StagedData[_files.Length];
        for (var i = 0; i < files.Length; i++)
        {
            files[i] = _files[i] ?? throw new BethesdaArchiveException($"The Tes3 archive has no file with index {i}");
            data[i] = _data[i] ?? throw new BethesdaArchiveException($"The Tes3 archive has no data for index {i}");
        }

        var header = new MemoryStream();
        using (var w = new BinaryWriter(header, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            w.Write(_state.VersionNumber);
            w.Write(_state.HashOffset);
            w.Write(_state.FileCount);
            foreach (var file in files)
            {
                w.Write(file.Size);
                w.Write(file.Offset);
            }

            foreach (var file in files) w.Write(file.NameOffset);

            var namesStart = header.Length;
            foreach (var file in files)
            {
                if (header.Length != namesStart + file.NameOffset)
                    throw new BethesdaArchiveException($"The name offsets of the Tes3 archive do not follow the names: {file.Path} is recorded at {file.NameOffset}");
                w.Write(ArchiveEncodings.Ascii.GetBytes(file.Path.ToString().Replace('/', '\\')));
                w.Write((byte)0);
            }

            if (header.Length != HeaderLength + _state.HashOffset)
                throw new BethesdaArchiveException($"The Tes3 hash table would start at {header.Length - HeaderLength}, but the state records {_state.HashOffset}");
            foreach (var file in files)
            {
                w.Write(file.Hash1);
                w.Write(file.Hash2);
            }

            if (header.Length != _state.DataOffset)
                throw new BethesdaArchiveException($"The Tes3 data would start at {header.Length}, but the state records {_state.DataOffset}");
        }

        await output.WriteAsync(header.GetBuffer().AsMemory(0, (int)header.Length), cancellationToken);

        // The data block is written front to back, so the output does not have to seek; a gap between two
        // recorded offsets is zero filled.
        long written = 0;
        var zeros = new byte[4096];
        foreach (var index in Enumerable.Range(0, files.Length).OrderBy(i => files[i].Offset))
        {
            var gap = files[index].Offset - written;
            if (gap < 0)
                throw new BethesdaArchiveException($"{files[index].Path} overlaps the file before it in the Tes3 archive");
            for (; gap > 0; gap -= Math.Min(gap, zeros.Length))
                await output.WriteAsync(zeros.AsMemory(0, (int)Math.Min(gap, zeros.Length)), cancellationToken);

            await data[index].CopyTo(output, cancellationToken);
            written = files[index].Offset + data[index].Length;
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var data in _data)
        {
            if (data is not null) await data.DisposeAsync();
        }
    }
}
