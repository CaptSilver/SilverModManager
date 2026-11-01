using System.Collections.Concurrent;
using NexusMods.Hashing.xxHash3;
using SilverModManager.Library;
using SilverModManager.MnemonicDB.Abstractions;
using NexusMods.Paths;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.Library;
using SilverModManager.Wabbajack.Format;
using SilverModManager.Wabbajack.Install;
using SilverModManager.Wabbajack.Library;

namespace SilverModManager.Wabbajack.Acquire;

/// <summary>A file a directive reads, found in the file store. <see cref="Open"/> can be called any number of times.</summary>
public sealed record ResolvedMember(FileIdentity StoreIdentity, FileIdentity? XxHash64, Size Size, Func<Task<Stream>> Open);

/// <summary>Thrown when an <see cref="ArchiveHashPath"/> names something the library cannot supply.</summary>
public sealed class MemberNotFoundException(string what) : Exception(what);

/// <summary>
/// Opens an archive kind the library does not expand itself (a Bethesda archive, say) to reach one member.
/// </summary>
public interface INestedArchiveOpener
{
    bool CanOpen(RelativePath name);

    /// <summary>The bytes of <paramref name="member"/>, or null when the archive has no such member.</summary>
    Task<Stream?> Open(Stream archive, RelativePath member, CancellationToken cancellationToken);
}

/// <summary>
/// Turns a directive's <see cref="ArchiveHashPath"/> into bytes in the file store. Create one per job: it keeps one
/// case-folded index of the entries per archive, built on first use, so resolving thousands of directives against one
/// archive reads its entry rows once. Bytes it backs up are pinned against garbage collection until it is disposed,
/// since nothing in the database references them until the install writes the loadout file that uses them.
/// </summary>
public sealed class ArchivePathResolver(
    ArchivePlan plan,
    IDb db,
    IFileStore store,
    GameFileSourceLookup gameFiles,
    IEnumerable<INestedArchiveOpener> openers) : IDisposable
{
    private const int HashBufferSize = 1024 * 1024;

    private readonly LibraryArchiveLocator _locator = new();
    private readonly LibraryFileDigests _digests = new(store, db.Connection);
    private readonly INestedArchiveOpener[] _openers = openers.ToArray();
    // The plan's own lookup scans every row, once per directive.
    private readonly Lazy<Dictionary<FileIdentity, ArchiveEntry>> _archives = new(() =>
    {
        var byHash = new Dictionary<FileIdentity, ArchiveEntry>();
        foreach (var row in plan.Rows) byHash.TryAdd(row.Archive.XxHash64, row.Archive);
        return byHash;
    });
    private readonly ConcurrentDictionary<EntityId, Lazy<Dictionary<string, LibraryArchiveFileEntry.ReadOnly>>> _indexes = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<ResolvedMember>>> _stored = new();
    private readonly ConcurrentDictionary<EntityId, FileIdentity> _hashed = new();
    private readonly List<(EntityId File, FileIdentity XxHash64)> _unrecorded = [];
    private readonly SemaphoreSlim _recordLock = new(1, 1);
    private readonly List<IDisposable> _pins = [];
    private bool _disposed;
    private int _indexesBuilt;

    /// <summary>
    /// How many hashes are computed for library files before they are written to the library in one commit. A commit
    /// per file makes the loadout react to each one.
    /// </summary>
    private const int RecordBatchSize = 500;

    /// <summary>How many entry indexes have been built, one per archive that had a member looked up in it.</summary>
    public int IndexesBuilt => Volatile.Read(ref _indexesBuilt);

    public async Task<ResolvedMember> Resolve(ArchiveHashPath path, CancellationToken cancellationToken)
    {
        if (_archives.Value.TryGetValue(path.ArchiveXxHash64, out var planned) && planned.State is GameFileSourceState gameFile)
        {
            var source = await ResolveGameFile(gameFile, cancellationToken);
            if (path.Parts.Count == 0) return source;
            return await ThroughOpener(gameFile.GameFile.ToString(), source.StoreIdentity, path.Parts, cancellationToken);
        }

        var library = _locator.FindByXxHash64(db, path.ArchiveXxHash64)
            ?? throw new MemberNotFoundException($"the library has no file with xxHash64 {WabbajackHash.ToBase64(path.ArchiveXxHash64)}");

        if (path.Parts.Count == 0) return await FromLibraryFile(library, library.FileName.ToString(), cancellationToken);

        var current = library;
        for (var i = 0; i < path.Parts.Count; i++)
        {
            var part = path.Parts[i];
            if (!current.TryGetAsLibraryArchive(out var archive))
            {
                if (!await store.HaveFile(current.Identity))
                    throw new MemberNotFoundException($"'{current.FileName}' is not kept in the file store");
                return await ThroughOpener(current.FileName.ToString(), current.Identity, path.Parts.Skip(i).ToArray(), cancellationToken);
            }

            if (!Entries(archive).TryGetValue(Fold(part), out var entry))
                throw new MemberNotFoundException($"'{part}' is not in '{current.FileName}'");
            current = entry.AsLibraryFile();
        }

        return await FromLibraryFile(current, path.Parts[^1].ToString(), cancellationToken);
    }

    private static string Fold(RelativePath path) => path.ToString().Replace('\\', '/').ToLowerInvariant();

    private Dictionary<string, LibraryArchiveFileEntry.ReadOnly> Entries(LibraryArchive.ReadOnly archive)
    {
        // Lazy, so threads that need the same archive wait for one build instead of each making their own.
        return _indexes.GetOrAdd(archive.Id, _ => new Lazy<Dictionary<string, LibraryArchiveFileEntry.ReadOnly>>(() =>
        {
            var index = new Dictionary<string, LibraryArchiveFileEntry.ReadOnly>();
            foreach (var entry in LibraryArchiveFileEntry.FindByParent(db, archive))
            {
                // Two entries that differ only in case collapse to the first, as the install tree will.
                index.TryAdd(Fold(entry.Path), entry);
            }
            Interlocked.Increment(ref _indexesBuilt);
            return index;
        })).Value;
    }

    private async Task<ResolvedMember> FromLibraryFile(LibraryFile.ReadOnly file, string name, CancellationToken cancellationToken)
    {
        // The store keeps no bytes for an archive the library expanded; only its members are there.
        if (!await store.HaveFile(file.Identity))
            throw new MemberNotFoundException($"'{name}' is an archive the library expanded, so its own bytes are not kept");

        var xxHash64 = await HashOf(file, cancellationToken);
        return new ResolvedMember(file.Identity, xxHash64, file.Size, async () => await store.GetFileStream(file.Identity, cancellationToken));
    }

    /// <summary>
    /// The file's xxHash64, computed from the store when the row lacks it. A computed hash is kept for the next lookup and
    /// queued for the library, which gets them in batches (see <see cref="FlushDigests"/>).
    /// </summary>
    private async Task<FileIdentity?> HashOf(LibraryFile.ReadOnly file, CancellationToken cancellationToken)
    {
        if (file.XxHash64Identity() is { } recorded) return recorded;
        if (_hashed.TryGetValue(file.Id, out var known)) return known;

        if (await _digests.ComputeXxHash64(file, cancellationToken) is not { } computed) return null;
        if (!computed.NeedsRecording) return computed.XxHash64;
        if (!_hashed.TryAdd(file.Id, computed.XxHash64)) return computed.XxHash64;

        List<(EntityId, FileIdentity)>? batch = null;
        lock (_unrecorded)
        {
            _unrecorded.Add((file.Id, computed.XxHash64));
            if (_unrecorded.Count >= RecordBatchSize)
            {
                batch = [.. _unrecorded];
                _unrecorded.Clear();
            }
        }
        if (batch is not null) await Record(batch);
        return computed.XxHash64;
    }

    private async Task Record(IReadOnlyCollection<(EntityId, FileIdentity)> batch)
    {
        // One writer at a time; the commits are what the loadout reacts to, and they are few.
        await _recordLock.WaitAsync();
        try
        {
            await _digests.RecordXxHash64(batch);
        }
        finally
        {
            _recordLock.Release();
        }
    }

    /// <summary>Writes the hashes computed for library files that lacked one to the library, in one commit.</summary>
    public async Task FlushDigests(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        List<(EntityId, FileIdentity)> batch;
        lock (_unrecorded)
        {
            batch = [.. _unrecorded];
            _unrecorded.Clear();
        }
        await Record(batch);
    }

    /// <summary>
    /// The last stop for a path that goes into something the library has not expanded (a member, or a game file): ask
    /// whichever opener knows the kind, and keep what it produces in the store so later reads are plain store reads.
    /// </summary>
    private Task<ResolvedMember> ThroughOpener(string containerName, FileIdentity containerHash, IReadOnlyList<RelativePath> remaining, CancellationToken cancellationToken)
    {
        var name = RelativePath.FromUnsanitizedInput(containerName);
        var opener = _openers.FirstOrDefault(o => o.CanOpen(name))
            ?? throw new MemberNotFoundException($"cannot look for '{remaining[0]}' inside '{containerName}': this build cannot open {name.Extension} files");
        if (remaining.Count != 1)
            throw new MemberNotFoundException($"cannot look for '{remaining[1]}' inside '{remaining[0]}' inside '{containerName}': archives nested in {name.Extension} files are not supported");

        var member = remaining[0];
        async Task<Stream> OpenMember()
        {
            await using var archive = await store.GetFileStream(containerHash, cancellationToken);
            return await opener.Open(archive, member, cancellationToken)
                ?? throw new MemberNotFoundException($"'{member}' is not in '{containerName}'");
        }

        return Store($"{containerHash}/{Fold(member)}", OpenMember, cancellationToken);
    }

    /// <summary>
    /// The file in the game folder (or its backup) as the list's source. Kept in the file store on first use so a later
    /// Apply that replaces the live file cannot change what the install reads.
    /// </summary>
    private Task<ResolvedMember> ResolveGameFile(GameFileSourceState state, CancellationToken cancellationToken)
    {
        async Task<Stream> OpenGameFile()
        {
            try
            {
                return (await gameFiles.Open(state.GameFile, cancellationToken)).Stream;
            }
            catch (FileNotFoundException e)
            {
                throw new MemberNotFoundException(e.Message);
            }
        }

        return Store($"game:{Fold(state.GameFile)}", OpenGameFile, cancellationToken);
    }

    /// <summary>
    /// Hashes what <paramref name="open"/> yields, backs it up unless the store has it, and returns a member that reads
    /// it back from the store. Done once per <paramref name="key"/>: threads asking for the same key wait for the one
    /// doing it, and different keys go ahead together. <paramref name="open"/> is called once; the stream is hashed and,
    /// when the store lacks the bytes, read again from the start.
    /// </summary>
    private Task<ResolvedMember> Store(string key, Func<Task<Stream>> open, CancellationToken cancellationToken)
    {
        var entry = _stored.GetOrAdd(key, _ => new Lazy<Task<ResolvedMember>>(() => StoreOnce(key, open)));
        return entry.Value.WaitAsync(cancellationToken);
    }

    private async Task<ResolvedMember> StoreOnce(string key, Func<Task<Stream>> open)
    {
        try
        {
            return await StoreBytes(await OpenSeekable(open));
        }
        catch
        {
            // A failure is not remembered: the next ask tries again.
            _stored.TryRemove(key, out _);
            throw;
        }
    }

    private async Task<ResolvedMember> StoreBytes(Stream source)
    {
        var cancellationToken = CancellationToken.None;
        FileIdentity xxHash3, xxHash64;
        Size size;
        await using (source)
        {
            (xxHash3, xxHash64, size) = await StreamDigests.HashAndCount(source, cancellationToken);
            source.Position = 0;

            // Pinned first, so every collection that marks from here on keeps the bytes. Deduplicating inside
            // BackupFiles checks for them under the store's read lock, after any collection already running (which
            // marked before the pin and may drop bytes the store still lists) has finished.
            Pin(xxHash3);
            await store.BackupFiles([new ArchivedFileEntry(new OneShotStreamFactory(source), xxHash3, size)], deduplicate: true, cancellationToken);
        }

        return new ResolvedMember(xxHash3, xxHash64, size, async () => await store.GetFileStream(xxHash3, cancellationToken));
    }

    private void Pin(FileIdentity identity)
    {
        var pin = FileStorePins.Pin(identity);
        lock (_pins)
        {
            if (!_disposed)
            {
                _pins.Add(pin);
                return;
            }
        }

        // A store still in flight when the job gave up and disposed the resolver; nothing would release this pin.
        pin.Dispose();
    }

    /// <summary>Releases the pins on everything this resolver backed up.</summary>
    public void Dispose()
    {
        lock (_pins)
        {
            _disposed = true;
            foreach (var pin in _pins) pin.Dispose();
            _pins.Clear();
        }
    }

    /// <summary>
    /// The store's packer reads its inputs by offset, so a stream that cannot seek (a decompressor an opener hands back,
    /// say) is spooled to a temp file first.
    /// </summary>
    private static async Task<Stream> OpenSeekable(Func<Task<Stream>> open)
    {
        var source = await open();
        if (source.CanSeek) return source;

        await using (source)
        {
            var spool = new FileStream(System.IO.Path.GetTempFileName(), FileMode.Open, FileAccess.ReadWrite, FileShare.None,
                bufferSize: HashBufferSize, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
            try
            {
                await source.CopyToAsync(spool);
                spool.Position = 0;
                return spool;
            }
            catch
            {
                await spool.DisposeAsync();
                throw;
            }
        }
    }

    /// <summary>Hands the store the stream already open, positioned at the start; the caller disposes it.</summary>
    private sealed class OneShotStreamFactory(Stream stream) : IStreamFactory
    {
        public RelativePath FileName => RelativePath.Empty;

        public ValueTask<Stream> GetStreamAsync()
        {
            stream.Position = 0;
            return ValueTask.FromResult<Stream>(new LeaveOpenStream(stream));
        }
    }
}
