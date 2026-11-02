using System.Collections.Concurrent;
using SilverModManager.Games.Bethesda.Archives;
using NexusMods.Paths;

namespace SilverModManager.Wabbajack.Acquire;

/// <summary>
/// Reads one member out of a Bethesda archive (<c>.bsa</c> or <c>.ba2</c>) for a list path that goes through it: the
/// library does not expand these, but lists copy single textures and meshes out of them, including out of the game's own.
/// </summary>
public sealed class BsaMemberOpener : INestedArchiveOpener
{
    /// <summary>
    /// How many readers an opened archive keeps, so how many threads extract from one archive at once. Each holds its
    /// own entry table and a stream of the container whose cache of decompressed chunks can reach 16 MiB, so the count
    /// caps what a large archive costs while it is open (about 20 MB a reader). A list draws most members from a few
    /// large archives, where one reader extracts about 400 MB/s of compressed members and the gain flattens at four
    /// (BsaMemberOpenerBenchmarks), so four on a machine with the cores for it and two on a small one.
    /// </summary>
    internal static readonly int DefaultReadersPerArchive = Math.Clamp(Environment.ProcessorCount / 4, 2, 4);

    private readonly int _readersPerArchive;

    public BsaMemberOpener() : this(DefaultReadersPerArchive)
    {
    }

    internal BsaMemberOpener(int readersPerArchive)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(readersPerArchive, 1);
        _readersPerArchive = readersPerArchive;
    }

    // Under the large object heap's 85 KB threshold: a buffer per member copy would otherwise be a LOH array each.
    private const int BufferSize = 64 * 1024;

    public bool CanOpen(RelativePath name)
        => name.Extension == new Extension(".bsa") || name.Extension == new Extension(".ba2");

    /// <summary>Reads the archive's index once; its readers, one for each thread reading at the same time, are kept until it is disposed.</summary>
    public Task<INestedArchive> OpenArchive(Func<CancellationToken, Task<Stream>> openContainer, CancellationToken cancellationToken)
        => Task.FromResult<INestedArchive>(new OpenedArchive(openContainer, _readersPerArchive));

    public async Task<IReadOnlyList<RelativePath>> ListMembers(Stream archive, CancellationToken cancellationToken)
    {
        var reader = await OpenReader(archive, "list its members", cancellationToken);
        await using (reader.Reader)
            return reader.Entries.Values.Select(e => e.Path).ToArray();
    }

    public async Task<Stream?> Open(Stream archive, RelativePath member, CancellationToken cancellationToken)
    {
        var reader = await OpenReader(archive, $"look for '{member}'", cancellationToken);
        await using (reader.Reader)
            return await Copy(reader.Entries, member, cancellationToken);
    }

    /// <summary>A reader over one stream of the archive and its entries by folded path, the first of two that differ only in case.</summary>
    private sealed record ArchiveReader(IBethesdaArchiveReader Reader, Dictionary<string, IBethesdaArchiveEntry> Entries);

    private static async Task<ArchiveReader> OpenReader(Stream archive, string purpose, CancellationToken cancellationToken)
    {
        // The readers seek. A store stream that cannot is spooled; the reader then owns (and disposes) the spool.
        var source = archive.CanSeek ? archive : await Spool(archive, cancellationToken);

        IBethesdaArchiveReader reader;
        try
        {
            reader = await BethesdaArchive.Open(source, cancellationToken);
        }
        catch (BethesdaArchiveException e)
        {
            await source.DisposeAsync();
            throw new MemberNotFoundException($"cannot {purpose}: {e.Message}");
        }
        catch
        {
            // Cancelled or failed to read: nothing else holds the stream, and it can be a file handle and a chunk cache.
            await source.DisposeAsync();
            throw;
        }

        try
        {
            var entries = new Dictionary<string, IBethesdaArchiveEntry>(reader.Entries.Count);
            foreach (var entry in reader.Entries) entries.TryAdd(Fold(entry.Path.ToString()), entry);
            return new ArchiveReader(reader, entries);
        }
        catch
        {
            await reader.DisposeAsync();
            throw;
        }
    }

    private static async Task<Stream?> Copy(Dictionary<string, IBethesdaArchiveEntry> entries, RelativePath member, CancellationToken cancellationToken)
    {
        if (!entries.TryGetValue(Fold(member.ToString()), out var entry)) return null;

        // Returned to the resolver, which hashes it and stores it, so it has to outlive the archive.
        var copy = new FileStream(System.IO.Path.GetTempFileName(), FileMode.Open, FileAccess.ReadWrite, FileShare.None,
            BufferSize, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        try
        {
            await entry.CopyToAsync(copy, cancellationToken);
            copy.Position = 0;
            return copy;
        }
        catch (BethesdaArchiveException e)
        {
            await copy.DisposeAsync();
            throw new MemberNotFoundException($"cannot read '{member}': {e.Message}");
        }
        catch
        {
            await copy.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// An archive whose index is read once per reader. Reads on one reader are one at a time (they share a stream),
    /// so a reader is lent to one member's copy and taken back. Threads beyond the reader count wait
    /// for a reader instead of opening another.
    /// </summary>
    private sealed class OpenedArchive(Func<CancellationToken, Task<Stream>> openContainer, int readers) : INestedArchive
    {
        private readonly ConcurrentBag<ArchiveReader> _idle = [];
        private readonly SemaphoreSlim _readers = new(readers);
        private int _disposed;

        public async Task<Stream?> Open(RelativePath member, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
            await _readers.WaitAsync(cancellationToken);
            try
            {
                return await OpenWithReader(member, cancellationToken);
            }
            finally
            {
                _readers.Release();
            }
        }

        private async Task<Stream?> OpenWithReader(RelativePath member, CancellationToken cancellationToken)
        {
            if (!_idle.TryTake(out var reader))
                reader = await OpenReader(await openContainer(cancellationToken), $"look for '{member}'", cancellationToken);

            var healthy = false;
            try
            {
                var copy = await Copy(reader.Entries, member, cancellationToken);
                healthy = true;
                return copy;
            }
            finally
            {
                if (healthy && Volatile.Read(ref _disposed) == 0) _idle.Add(reader);
                else await reader.Reader.DisposeAsync();
            }
        }

        public async ValueTask DisposeAsync()
        {
            Volatile.Write(ref _disposed, 1);
            while (_idle.TryTake(out var reader)) await reader.Reader.DisposeAsync();
        }
    }

    private static async Task<Stream> Spool(Stream archive, CancellationToken cancellationToken)
    {
        var spool = new FileStream(System.IO.Path.GetTempFileName(), FileMode.Open, FileAccess.ReadWrite, FileShare.None,
            BufferSize, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        try
        {
            await archive.CopyToAsync(spool, cancellationToken);
            spool.Position = 0;
            return spool;
        }
        catch
        {
            await spool.DisposeAsync();
            throw;
        }
    }

    private static string Fold(string path) => path.Replace('\\', '/').ToLowerInvariant();
}
