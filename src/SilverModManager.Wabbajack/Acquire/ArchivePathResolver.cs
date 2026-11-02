using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
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

    /// <summary>
    /// Opens the archive once so that many members can be read from it without reading its index again. The
    /// container is opened with <paramref name="openContainer"/>, as often as the returned archive needs a stream of
    /// it. The default reads each member with <see cref="Open"/> over a fresh stream, which is right for an opener
    /// that has no index to keep.
    /// </summary>
    Task<INestedArchive> OpenArchive(Func<CancellationToken, Task<Stream>> openContainer, CancellationToken cancellationToken)
        => Task.FromResult<INestedArchive>(new PerMemberArchive(this, openContainer));
}

/// <summary>An archive opened by <see cref="INestedArchiveOpener.OpenArchive"/>. Members can be read from several threads at once.</summary>
public interface INestedArchive : IAsyncDisposable
{
    /// <summary>The bytes of <paramref name="member"/>, or null when the archive has no such member.</summary>
    Task<Stream?> Open(RelativePath member, CancellationToken cancellationToken);
}

/// <summary>Reads every member through <see cref="INestedArchiveOpener.Open"/> over a stream of the container opened for it alone.</summary>
internal sealed class PerMemberArchive(INestedArchiveOpener opener, Func<CancellationToken, Task<Stream>> openContainer) : INestedArchive
{
    public async Task<Stream?> Open(RelativePath member, CancellationToken cancellationToken)
    {
        await using var container = await openContainer(cancellationToken);
        return await opener.Open(container, member, cancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Turns a directive's <see cref="ArchiveHashPath"/> into bytes in the file store. Create one per job: it keeps one
/// case-folded index of the entries per archive, built on first use, so resolving thousands of directives against one
/// archive reads its entry rows once. Bytes it backs up are pinned against garbage collection until it is disposed,
/// since nothing in the database references them until the install writes the loadout file that uses them. Copying a
/// member into the store belongs to the resolver, not to the call that asked for it: a call that gives up stops
/// waiting, and <see cref="DisposeAsync"/> stops the copies still running and waits for them before releasing the pins.
/// </summary>
public sealed class ArchivePathResolver(
    ArchivePlan plan,
    IDb db,
    IFileStore store,
    GameFileSourceLookup gameFiles,
    IEnumerable<INestedArchiveOpener> openers,
    ILogger logger) : IDisposable, IAsyncDisposable
{
    /// <summary>How long <see cref="DisposeAsync"/> waits for copies still running before it gives up on them.</summary>
    internal TimeSpan CopyDrainTimeout { get; init; } = TimeSpan.FromSeconds(30);

    // Under the large object heap's 85 KB threshold: a spool per member would otherwise be a LOH array each.
    private const int HashBufferSize = 64 * 1024;

    /// <summary>How many opened archives are kept for the next member (see <see cref="NestedArchiveCache"/>).</summary>
    private const int OpenArchivesKept = 8;

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
    private readonly CancellationTokenSource _lifetime = new();
    private readonly NestedArchiveCache _openArchives = new(OpenArchivesKept);
    private BackupBatcher Backups => _backupBatcher ??= new BackupBatcher(store, _lifetime.Token);
    private BackupBatcher? _backupBatcher;
    // Each running copy with the store key it is for, so a log line can name what is still going.
    private readonly ConcurrentDictionary<Task, string> _copies = new();
    private bool _disposed;
    private bool _abandoned;
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
        async Task<Stream> OpenMember(CancellationToken copyToken)
        {
            // Opening the archive reads its index; one opened for an earlier member is reused.
            await using var lease = await _openArchives.Acquire(containerHash.ToString(),
                () => opener.OpenArchive(openToken => store.GetFileStream(containerHash, openToken), _lifetime.Token));
            return await lease.Archive.Open(member, copyToken)
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
        async Task<Stream> OpenGameFile(CancellationToken copyToken)
        {
            try
            {
                return (await gameFiles.Open(state.GameFile, copyToken)).Stream;
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
    private Task<ResolvedMember> Store(string key, Func<CancellationToken, Task<Stream>> open, CancellationToken cancellationToken)
    {
        var entry = _stored.GetOrAdd(key, _ => new Lazy<Task<ResolvedMember>>(() => Track(key, StoreOnce(key, open))));
        return entry.Value.WaitAsync(cancellationToken);
    }

    /// <summary>Remembers a copy until it ends, for <see cref="DisposeAsync"/> to wait for.</summary>
    private Task<ResolvedMember> Track(string key, Task<ResolvedMember> copy)
    {
        _copies[copy] = key;
        _ = copy.ContinueWith(done =>
        {
            _copies.TryRemove(done, out _);
            if (Volatile.Read(ref _abandoned))
            {
                // Nothing is waiting any more and its pin is gone; whatever it stored is unreferenced and the next
                // collection of the store takes it. Said so that a late write is not a silent one.
                logger.LogWarning("The copy of '{Key}' ended after the install stopped waiting for it ({Outcome}); anything it stored is unpinned", key, done.Status);
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return copy;
    }

    private async Task<ResolvedMember> StoreOnce(string key, Func<CancellationToken, Task<Stream>> open)
    {
        // Not the asking call's token: that call may give up while others wait for the same copy.
        var cancellationToken = _lifetime.Token;
        try
        {
            return await StoreBytes(await OpenSeekable(open, cancellationToken), cancellationToken);
        }
        catch
        {
            // A failure is not remembered: the next ask tries again.
            _stored.TryRemove(key, out _);
            throw;
        }
    }

    private async Task<ResolvedMember> StoreBytes(Stream source, CancellationToken cancellationToken)
    {
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
            await Backups.Add(new ArchivedFileEntry(new OneShotStreamFactory(source), xxHash3, size));
        }

        return new ResolvedMember(xxHash3, xxHash64, size, async () => await store.GetFileStream(xxHash3, CancellationToken.None));
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

    /// <summary>
    /// Stops the copies still running, waits for them for <see cref="CopyDrainTimeout"/>, then releases the pins on
    /// everything this resolver backed up. A copy that honours its token leaves nothing pinned and nothing running
    /// against the store. One that does not (the store's packer cannot be interrupted once it is building) is named in
    /// a warning and left to finish on its own, so the install job can return; its pin is released with the rest, and
    /// what it stores when it ends is unreferenced bytes the next store collection removes.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();
        var running = _copies.Keys.ToArray();
        if (running.Length > 0)
        {
            try
            {
                await Task.WhenAll(running.Select(Ended)).WaitAsync(CopyDrainTimeout);
            }
            catch (TimeoutException)
            {
                Volatile.Write(ref _abandoned, true);
                var names = _copies.Values.ToArray();
                logger.LogWarning("Stopped waiting after {Seconds} s for {Count} archive member copies that have not stopped: {Keys}",
                    CopyDrainTimeout.TotalSeconds, names.Length, string.Join(", ", names));
            }
        }
        await _openArchives.DisposeAsync();
        Dispose();
    }

    private static async Task Ended(Task copy)
    {
        try { await copy; } catch { /* its failure went to whoever asked for it; here it only has to be over */ }
    }

    /// <summary>Stops the copies still running and releases the pins without waiting for them; prefer <see cref="DisposeAsync"/>.</summary>
    public void Dispose()
    {
        _lifetime.Cancel();
        // On the pool, so the disposal's continuations cannot need the thread that is blocked waiting for them (a UI thread).
        Task.Run(() => _openArchives.DisposeAsync().AsTask()).GetAwaiter().GetResult();
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
    private static async Task<Stream> OpenSeekable(Func<CancellationToken, Task<Stream>> open, CancellationToken cancellationToken)
    {
        var source = await open(cancellationToken);
        if (source.CanSeek) return source;

        await using (source)
        {
            var spool = new FileStream(System.IO.Path.GetTempFileName(), FileMode.Open, FileAccess.ReadWrite, FileShare.None,
                bufferSize: HashBufferSize, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
            try
            {
                await source.CopyToAsync(spool, cancellationToken);
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
