using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using NexusMods.Sdk.FileStore;
using NexusMods.Sdk.IO;
using NexusMods.Wabbajack.Acquire;

namespace NexusMods.Wabbajack.Install;

/// <summary>A generated file waiting in a temp file to be stored. Disposing deletes the temp file.</summary>
public sealed class GeneratedFile : IAsyncDisposable
{
    private readonly TemporaryPath _temporary;

    internal GeneratedFile(TemporaryPath temporary, Hash xxHash3, Hash xxHash64, Size size)
    {
        _temporary = temporary;
        XxHash3 = xxHash3;
        XxHash64 = xxHash64;
        Size = size;
    }

    public AbsolutePath Path => _temporary.Path;

    /// <summary>The key the file has in the file store.</summary>
    public Hash XxHash3 { get; }

    /// <summary>The value a modlist records for it.</summary>
    public Hash XxHash64 { get; }

    public Size Size { get; }

    public ValueTask DisposeAsync() => _temporary.DisposeAsync();
}

/// <summary>
/// Turns bytes the install produced (a blob, a patched file, a built archive) into file store entries: written to a
/// temp file, hashed once for both digests, then backed up in batches so a big list does not hold thousands of
/// temp files at once.
/// </summary>
public sealed class GeneratedFileWriter(IFileStore store, TemporaryFileManager temporaryFiles)
{
    /// <summary>How many files go to the file store in one call.</summary>
    public const int StoreBatchSize = 200;

    public async Task<GeneratedFile> Generate(Func<Stream, Task> produce, CancellationToken cancellationToken)
    {
        var temporary = temporaryFiles.CreateFile();
        try
        {
            await using (var output = temporary.Path.Create())
            {
                await produce(output);
            }

            await using var input = temporary.Path.Read();
            var (xxHash3, xxHash64, size) = await StreamDigests.HashAndCount(input, cancellationToken);
            return new GeneratedFile(temporary, xxHash3, xxHash64, size);
        }
        catch
        {
            await temporary.DisposeAsync();
            throw;
        }
    }

    /// <summary>A temp file holding what <paramref name="produce"/> wrote, for something that has to read it back (an input of a built archive).</summary>
    public async Task<TemporaryPath> WriteTemporary(Func<Stream, Task> produce)
    {
        var temporary = temporaryFiles.CreateFile();
        try
        {
            await using var output = temporary.Path.Create();
            await produce(output);
            return temporary;
        }
        catch
        {
            await temporary.DisposeAsync();
            throw;
        }
    }

    /// <summary>Backs the files up in the file store and deletes their temp files.</summary>
    public async Task Store(IReadOnlyList<GeneratedFile> files, CancellationToken cancellationToken)
    {
        foreach (var batch in files.Chunk(StoreBatchSize))
        {
            var entries = batch.Select(f => new ArchivedFileEntry(new NativeFileStreamFactory(f.Path), f.XxHash3, f.Size));
            await store.BackupFiles(entries, deduplicate: true, cancellationToken);
            foreach (var file in batch) await file.DisposeAsync();
        }
    }
}
