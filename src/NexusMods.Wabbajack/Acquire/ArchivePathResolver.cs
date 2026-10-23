using NexusMods.Hashing.xxHash3;
using NexusMods.Library;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk.FileStore;
using NexusMods.Sdk.Hashes;
using NexusMods.Sdk.IO;
using NexusMods.Sdk.Library;
using NexusMods.Wabbajack.Format;
using NexusMods.Wabbajack.Library;

namespace NexusMods.Wabbajack.Acquire;

/// <summary>A file a directive reads, found in the file store. <see cref="Open"/> can be called any number of times.</summary>
public sealed record ResolvedMember(Hash StoreHash, Hash? XxHash64, Size Size, Func<Task<Stream>> Open);

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
/// archive reads its entry rows once.
/// </summary>
public sealed class ArchivePathResolver(
    ArchivePlan plan,
    IDb db,
    IFileStore store,
    GameFileSourceLookup gameFiles,
    IEnumerable<INestedArchiveOpener> openers)
{
    private const int HashBufferSize = 1024 * 1024;

    private readonly LibraryArchiveLocator _locator = new();
    private readonly LibraryFileDigests _digests = new(store, db.Connection);
    private readonly INestedArchiveOpener[] _openers = openers.ToArray();
    private readonly Dictionary<EntityId, Dictionary<string, LibraryArchiveFileEntry.ReadOnly>> _indexes = new();
    private readonly Dictionary<string, ResolvedMember> _stored = new();
    private readonly SemaphoreSlim _storeLock = new(1, 1);

    /// <summary>How many entry indexes have been built, one per archive that had a member looked up in it.</summary>
    public int IndexesBuilt { get; private set; }

    public async Task<ResolvedMember> Resolve(ArchiveHashPath path, CancellationToken cancellationToken)
    {
        if (plan.FindArchive(path.ArchiveXxHash64) is { State: GameFileSourceState gameFile })
        {
            var source = await ResolveGameFile(gameFile, cancellationToken);
            if (path.Parts.Count == 0) return source;
            return await ThroughOpener(gameFile.GameFile.ToString(), source.StoreHash, path.Parts, cancellationToken);
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
                if (!await store.HaveFile(current.Hash))
                    throw new MemberNotFoundException($"'{current.FileName}' is not kept in the file store");
                return await ThroughOpener(current.FileName.ToString(), current.Hash, path.Parts.Skip(i).ToArray(), cancellationToken);
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
        if (_indexes.TryGetValue(archive.Id, out var existing)) return existing;

        var index = new Dictionary<string, LibraryArchiveFileEntry.ReadOnly>();
        foreach (var entry in LibraryArchiveFileEntry.FindByParent(db, archive))
        {
            // Two entries that differ only in case collapse to the first, as the install tree will.
            index.TryAdd(Fold(entry.Path), entry);
        }
        _indexes[archive.Id] = index;
        IndexesBuilt++;
        return index;
    }

    private async Task<ResolvedMember> FromLibraryFile(LibraryFile.ReadOnly file, string name, CancellationToken cancellationToken)
    {
        // The store keeps no bytes for an archive the library expanded; only its members are there.
        if (!await store.HaveFile(file.Hash))
            throw new MemberNotFoundException($"'{name}' is an archive the library expanded, so its own bytes are not kept");

        var xxHash64 = await _digests.EnsureXxHash64(file, cancellationToken);
        var hash = file.Hash;
        return new ResolvedMember(hash, xxHash64, file.Size, async () => await store.GetFileStream(hash, cancellationToken));
    }

    /// <summary>
    /// The last stop for a path that goes into something the library has not expanded (a member, or a game file): ask
    /// whichever opener knows the kind, and keep what it produces in the store so later reads are plain store reads.
    /// </summary>
    private Task<ResolvedMember> ThroughOpener(string containerName, Hash containerHash, IReadOnlyList<RelativePath> remaining, CancellationToken cancellationToken)
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
    /// it back from the store. Done once per <paramref name="key"/>: <paramref name="open"/> is called once to hash and,
    /// when the store lacks the bytes, once more to copy.
    /// </summary>
    private async Task<ResolvedMember> Store(string key, Func<Task<Stream>> open, CancellationToken cancellationToken)
    {
        await _storeLock.WaitAsync(cancellationToken);
        try
        {
            if (_stored.TryGetValue(key, out var known)) return known;

            Hash xxHash3, xxHash64;
            Size size;
            await using (var stream = await open())
            {
                (xxHash3, xxHash64, size) = await StreamDigests.HashAndCount(stream, cancellationToken);
            }

            if (!await store.HaveFile(xxHash3))
                await store.BackupFiles([new ArchivedFileEntry(new OpenerStreamFactory(open), xxHash3, size)], deduplicate: false, cancellationToken);

            var resolved = new ResolvedMember(xxHash3, xxHash64, size, async () => await store.GetFileStream(xxHash3, cancellationToken));
            _stored[key] = resolved;
            return resolved;
        }
        finally
        {
            _storeLock.Release();
        }
    }

    /// <summary>
    /// The store's packer reads its inputs by offset, so a stream that cannot seek (a decompressor an opener hands back,
    /// say) is spooled to a temp file first.
    /// </summary>
    private sealed class OpenerStreamFactory(Func<Task<Stream>> open) : IStreamFactory
    {
        public RelativePath FileName => RelativePath.Empty;

        public async ValueTask<Stream> GetStreamAsync()
        {
            var source = await open();
            if (source.CanSeek) return source;

            await using (source)
            {
                var spool = new FileStream(System.IO.Path.GetTempFileName(), FileMode.Open, FileAccess.ReadWrite, FileShare.None,
                    bufferSize: HashBufferSize, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
                await source.CopyToAsync(spool);
                spool.Position = 0;
                return spool;
            }
        }
    }
}
