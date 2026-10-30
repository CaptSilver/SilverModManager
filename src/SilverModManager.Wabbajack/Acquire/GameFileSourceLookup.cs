using SilverModManager.Abstractions.Games.FileHashes;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Sdk.Loadouts;
using NexusMods.Hashing.xxHash3;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.BuiltInEntities;
using NexusMods.Paths;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using SilverModManager.Abstractions.Games.FileHashes.Models;

namespace SilverModManager.Wabbajack.Acquire;

/// <summary>
/// Reads a file of the game for a modlist that takes it as a source. A previous Apply may have replaced the live file,
/// so the original the app backed up wins when the live file is not that original; a loadout that never applied has no
/// backups and reads the game folder.
/// </summary>
/// <remarks>
/// <paramref name="installationId"/> is null for an installation the app has not managed yet; it has no backups, so
/// the game folder is all there is to read.
/// <para>
/// A file is only read when no recorded digest names it. The index already knows most live files (disk state, the
/// fingerprint rows of earlier reads, the store's attested SHA-1), and the recorded <see cref="HashRelation"/> rows
/// join those to the xxHash64 a list names files by. A read that cannot be avoided leaves a fingerprint row and a
/// relation behind, so the next lookup of the unchanged file reads nothing.
/// </para>
/// </remarks>
public sealed class GameFileSourceLookup(GameInstallation installation, EntityId? installationId, IConnection connection, IFileStore fileStore, IFileIdentityResolver identityResolver, IFileHashesService fileHashes)
{
    public GameFileSourceLookup(Loadout.ReadOnly loadout, IConnection connection, IFileStore fileStore, IFileIdentityResolver identityResolver, IFileHashesService fileHashes)
        : this(loadout.InstallationInstance, (EntityId)loadout.InstallationId, connection, fileStore, identityResolver, fileHashes) { }

    private readonly record struct Backup(FileIdentity Key, FileIdentity Original, Size Size);

    /// <summary>The bytes a lookup reads: the live file with the fingerprint it had, or the store's copy of the original.</summary>
    private readonly record struct Source(Backup? Backup, AbsolutePath Live, FileFingerprint Fingerprint, FileIdentity? KnownIdentity);

    private readonly record struct Described(FileIdentity XxHash3, FileIdentity XxHash64, Size Size);

    private readonly Lazy<Dictionary<GamePath, (FileIdentity Identity, Size Size, DateTimeOffset LastModified)>> _initialState
        = new(() => installationId is { } owner ? ReadDiskState(connection, owner, GameInstallMetadata.InitialDiskStateTransaction) : [], LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly Lazy<Dictionary<GamePath, (FileIdentity Identity, Size Size, DateTimeOffset LastModified)>> _currentState
        = new(() => installationId is { } owner ? ReadDiskState(connection, owner, GameInstallMetadata.LastScannedDiskStateTransaction) : [], LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly Lazy<FrozenDictionary<GamePath, AttestedLookup>> _attested
        = new(() => fileHashes.AttestedFiles(installation), LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly ConcurrentDictionary<RelativePath, Lazy<Task<Described?>>> _described = new();
    private readonly SemaphoreSlim _recording = new(1, 1);

    /// <summary>Opens a file of the game folder for a front-to-back read. Tests swap it to watch how many are open at once.</summary>
    internal Func<AbsolutePath, Stream> OpenLive { get; init; } = SequentialFileRead.Open;

    /// <summary>
    /// The version of this install, as far as the game can tell. Null when it cannot read one.
    /// </summary>
    public Version? InstalledVersion
    {
        get
        {
            var version = installation.Game.GetLocalVersion(installation);
            return version.HasValue ? version.Value : null;
        }
    }

    /// <summary>
    /// Opens the file at <paramref name="gameFile"/> (relative to the game folder) and returns its xxHash3 identity, which
    /// names it in the file store when the bytes came from there. Throws <see cref="FileNotFoundException"/> when neither a
    /// backup nor the live file exists.
    /// </summary>
    public async Task<(Stream Stream, FileIdentity XxHash3)> Open(RelativePath gameFile, CancellationToken cancellationToken = default)
    {
        var source = await Locate(gameFile)
            ?? throw new FileNotFoundException($"The game folder has no '{gameFile}'.");
        if (source.Backup is { } backup)
            return (await fileStore.GetFileStream(backup.Key, cancellationToken), backup.Key);

        // Described once and remembered, so opening a file a second time costs no extra pass over it.
        var described = await Describe(gameFile, cancellationToken)
            ?? throw new FileNotFoundException($"The game folder has no '{gameFile}'.");
        return (source.Live.Read(), described.XxHash3);
    }

    /// <summary>
    /// The digests and size of the file as <see cref="Open"/> would read it, or null when the game folder has no such
    /// file. The xxHash64 comes from the recorded digests when any of them names the file, else from one read.
    /// </summary>
    /// <param name="onRead">Told how many bytes each read of the file returned, so a caller can show how far a big file has got. Not called for a file that needs no read, or one already described.</param>
    public async Task<(FileIdentity XxHash3, FileIdentity XxHash64, Size Size)?> Describe(RelativePath gameFile, CancellationToken cancellationToken = default, Action<int>? onRead = null)
    {
        // Several archives can name one file; whichever asks first reads it and the rest take its answer.
        var entry = _described.GetOrAdd(gameFile, _ => new Lazy<Task<Described?>>(() => DescribeUncached(gameFile, onRead, cancellationToken)));
        try
        {
            return await entry.Value is { } result ? (result.XxHash3, result.XxHash64, result.Size) : null;
        }
        catch
        {
            // A cancelled or failed read is not an answer; the next caller tries again.
            _described.TryRemove(new KeyValuePair<RelativePath, Lazy<Task<Described?>>>(gameFile, entry));
            throw;
        }
    }

    private async Task<Described?> DescribeUncached(RelativePath gameFile, Action<int>? onRead, CancellationToken cancellationToken)
    {
        if (await Locate(gameFile) is not { } source) return null;

        var known = source.Backup?.Key ?? source.KnownIdentity;
        var size = source.Backup?.Size ?? source.Fingerprint.Size;
        if (known is { } identity
            && identityResolver.TryConvert(identity, size, HashKind.XxHash3, out var xxHash3)
            && identityResolver.TryConvert(identity, size, HashKind.XxHash64, out var xxHash64))
            return new Described(xxHash3, xxHash64, size);

        return source.Backup is { } backup
            ? await ReadBackup(backup, onRead, cancellationToken)
            : await ReadLive(gameFile, source, onRead, cancellationToken);
    }

    private async Task<Described> ReadBackup(Backup backup, Action<int>? onRead, CancellationToken cancellationToken)
    {
        await using var stored = await fileStore.GetFileStream(backup.Key, cancellationToken);
        var size = Size.FromLong(stored.Length);
        if (!stored.CanSeek)
        {
            var both = await Hasher.HashAsync(Counting(stored, onRead), cancellationToken: cancellationToken);
            return new Described(FileIdentity.XxHash3(both.Hash1), FileIdentity.XxHash64(both.Hash2), size);
        }

        var hashes = await MultiHasher.HashStream(Counting(stored, onRead), cancellationToken);
        await Remember(() => fileHashes.RecordHashRelations([hashes], CancellationToken.None), "backed-up original");
        return new Described(FileIdentity.XxHash3(hashes.XxHash3), FileIdentity.XxHash64(hashes.XxHash64), size);
    }

    private async Task<Described> ReadLive(RelativePath gameFile, Source source, Action<int>? onRead, CancellationToken cancellationToken)
    {
        MultiHash hashes;
        await using (var stream = OpenLive(source.Live))
            hashes = await MultiHasher.HashStream(Counting(stream, onRead), cancellationToken);

        // The rows outlive this lookup, so they are written only for bytes read under a fingerprint that held.
        if (FileFingerprint.Of(source.Live) == source.Fingerprint)
        {
            var file = new IndexedFileHash(new GamePath(LocationId.Game, gameFile), source.Fingerprint.Size, source.Fingerprint.LastModified, FileIdentity.XxHash3(hashes.XxHash3), hashes);
            await Remember(() => fileHashes.RecordIndexedFiles(installation, [file], CancellationToken.None), gameFile.ToString());
        }
        return new Described(FileIdentity.XxHash3(hashes.XxHash3), FileIdentity.XxHash64(hashes.XxHash64), source.Fingerprint.Size);
    }

    /// <summary>
    /// The rows only save a later lookup a read, so a store that cannot take them costs speed, not the answer.
    /// </summary>
    private async Task Remember(Func<Task> record, string what)
    {
        await _recording.WaitAsync(CancellationToken.None);
        try
        {
            await record();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            connection.ServiceProvider.GetRequiredService<ILogger<GameFileSourceLookup>>()
                .LogWarning(e, "Could not save the digests of {What}; the next lookup reads it again", what);
        }
        finally
        {
            _recording.Release();
        }
    }

    private static readonly MultiHasherBuilder<Hash, System.IO.Hashing.XxHash3, Xx3Hasher, Hash, System.IO.Hashing.XxHash64, Xx64Hasher> Hasher
        = MultiHasherBuilder.Start()
            .AddHasher<Hash, System.IO.Hashing.XxHash3, Xx3Hasher>()
            .AddHasher<Hash, System.IO.Hashing.XxHash64, Xx64Hasher>();

    private static Stream Counting(Stream stream, Action<int>? onRead) => onRead is null ? stream : new ReadCountingStream(stream, onRead);

    private AbsolutePath LivePath(RelativePath gameFile)
        => installation.Locations.ToAbsolutePath(new GamePath(LocationId.Game, gameFile));

    /// <summary>
    /// Picks the bytes <see cref="Open"/> and <see cref="Describe"/> read. The live file is the one to read when
    /// the index knows it and it is the original; otherwise the backup of the original, if there is one; otherwise
    /// the live file. Null when there is neither.
    /// </summary>
    private async ValueTask<Source?> Locate(RelativePath gameFile)
    {
        var live = LivePath(gameFile);
        var backup = await FindBackup(gameFile);
        if (!live.FileExists) return backup is { } only ? new Source(only, live, default, null) : null;

        var fingerprint = FileFingerprint.Of(live);
        var known = KnownIdentity(gameFile, fingerprint);
        if (backup is { } original
            && !(known is { } liveIdentity
                 && fingerprint.Size == original.Size
                 && identityResolver.Resolve(liveIdentity, original.Original, original.Size) == IdentityMatch.Same))
            return new Source(original, live, fingerprint, known);

        return new Source(null, live, fingerprint, known);
    }

    /// <summary>
    /// What the index already says the live file is, when it was recorded under the size and modified time the file has
    /// now: the disk state's entry, the fingerprint row of an earlier read, or the store's attested SHA-1.
    /// </summary>
    private FileIdentity? KnownIdentity(RelativePath gameFile, FileFingerprint fingerprint)
    {
        var path = new GamePath(LocationId.Game, gameFile);
        if (_currentState.Value.TryGetValue(path, out var entry) && Matches(entry.Size, entry.LastModified)) return entry.Identity;
        if (fileHashes.TryGetFingerprintedHash(installation, path, fingerprint.Size, fingerprint.LastModified, out var remembered)) return remembered;
        if (_attested.Value.TryGetValue(path, out var attested) && Matches(attested.Size, attested.ModifiedUtc)) return attested.Identity;
        return null;

        bool Matches(Size size, DateTimeOffset modified) => size == fingerprint.Size && modified.UtcTicks == fingerprint.LastModified.UtcTicks;
    }

    /// <summary>
    /// The store key of the file's original: the hash it had when the game was first indexed, if that copy was backed
    /// up for this install and the store still holds it.
    /// </summary>
    private async ValueTask<Backup?> FindBackup(RelativePath gameFile)
    {
        if (installationId is not { } owner) return null;
        if (!_initialState.Value.TryGetValue(new GamePath(LocationId.Game, gameFile), out var initial)) return null;
        if (await identityResolver.StoreKeyAsync(fileStore, initial.Identity, initial.Size) is not { } key) return null;

        var pinned = GameBackedUpFile.All(connection.Db)
            .Any(f => f.Identity.Equals(key) && f.GameInstallId.Value == owner);
        if (!pinned) return null;
        return new Backup(key, initial.Identity, initial.Size);
    }

    private static Dictionary<GamePath, (FileIdentity Identity, Size Size, DateTimeOffset LastModified)> ReadDiskState(IConnection connection, EntityId owner, ReferenceAttribute<Transaction> transaction)
    {
        var result = new Dictionary<GamePath, (FileIdentity Identity, Size Size, DateTimeOffset LastModified)>();
        var metadata = GameInstallMetadata.Load(connection.Db, owner);
        if (!transaction.TryGetValue(metadata, out var tx)) return result;

        var asOf = connection.AsOf(TxId.From(tx.Value));
        var oldMetadata = GameInstallMetadata.Load(asOf, owner);
        foreach (var entry in DiskStateEntry.FindByGame(asOf, oldMetadata))
        {
            GamePath path = entry.Path;
            if (path.LocationId == LocationId.Game) result[path] = (entry.IdentityOf(), entry.Size, entry.LastModified);
        }
        return result;
    }
}
