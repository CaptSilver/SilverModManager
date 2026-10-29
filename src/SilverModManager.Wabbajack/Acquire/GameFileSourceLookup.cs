using SilverModManager.Abstractions.Games.FileHashes;
using SilverModManager.Abstractions.Games.FileHashes.Models;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Sdk.Loadouts;
using NexusMods.Hashing.xxHash3;
using SilverModManager.MnemonicDB.Abstractions;
using NexusMods.Paths;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.Wabbajack.Acquire;

/// <summary>
/// Reads a file of the game for a modlist that takes it as a source. A previous Apply may have replaced the live file,
/// so the original the app backed up wins when there is one; a loadout that never applied has no backups and reads the
/// game folder.
/// </summary>
/// <remarks>
/// <paramref name="installationId"/> is null for an installation the app has not managed yet; it has no backups, so
/// the game folder is all there is to read.
/// </remarks>
public sealed class GameFileSourceLookup(GameInstallation installation, EntityId? installationId, IConnection connection, IFileStore fileStore, IFileHashesService fileHashes)
{
    public GameFileSourceLookup(Loadout.ReadOnly loadout, IConnection connection, IFileStore fileStore, IFileHashesService fileHashes)
        : this(loadout.InstallationInstance, (EntityId)loadout.InstallationId, connection, fileStore, fileHashes) { }

    private static readonly MultiHasherBuilder<Hash, System.IO.Hashing.XxHash3, Xx3Hasher, Hash, System.IO.Hashing.XxHash64, Xx64Hasher> Hasher
        = MultiHasherBuilder.Start()
            .AddHasher<Hash, System.IO.Hashing.XxHash3, Xx3Hasher>()
            .AddHasher<Hash, System.IO.Hashing.XxHash64, Xx64Hasher>();

    private Dictionary<GamePath, (FileIdentity Identity, Size Size)>? _initialState;
    private readonly Dictionary<RelativePath, (Hash XxHash3, Hash XxHash64, Size Size)?> _described = new();

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
    /// Opens the file at <paramref name="gameFile"/> (relative to the game folder) and returns its xxHash3, which is its
    /// key in the file store when the bytes came from there. Throws <see cref="FileNotFoundException"/> when neither a
    /// backup nor the live file exists.
    /// </summary>
    public async Task<(Stream Stream, Hash XxHash3)> Open(RelativePath gameFile, CancellationToken cancellationToken = default)
    {
        if (await FindBackup(gameFile) is { } backup)
            return (await fileStore.GetFileStream(backup, cancellationToken), backup);

        // Hashed once and remembered, so opening a file a second time costs no extra pass over it.
        var described = await Describe(gameFile, cancellationToken)
            ?? throw new FileNotFoundException($"The game folder has no '{gameFile}'.");
        return (LivePath(gameFile).Read(), described.XxHash3);
    }

    /// <summary>
    /// The digests and size of the file as <see cref="Open"/> would read it, or null when the game folder has no such
    /// file. The xxHash64 comes from the vanilla file hashes when the xxHash3 is known there, else from one read.
    /// </summary>
    public async Task<(Hash XxHash3, Hash XxHash64, Size Size)?> Describe(RelativePath gameFile, CancellationToken cancellationToken = default)
    {
        if (_described.TryGetValue(gameFile, out var cached)) return cached;

        var result = await DescribeUncached(gameFile, cancellationToken);
        _described[gameFile] = result;
        return result;
    }

    private async Task<(Hash, Hash, Size)?> DescribeUncached(RelativePath gameFile, CancellationToken cancellationToken)
    {
        if (await FindBackup(gameFile) is { } backup)
        {
            await using var stored = await fileStore.GetFileStream(backup, cancellationToken);
            var size = Size.FromLong(stored.Length);
            if (await KnownXxHash64(backup) is { } known) return (backup, known, size);

            var hashes = await Hasher.HashAsync(stored, cancellationToken: cancellationToken);
            return (hashes.Hash1, hashes.Hash2, size);
        }

        var live = LivePath(gameFile);
        if (!live.FileExists) return null;

        await using var stream = SequentialFileRead.Open(live);
        var both = await Hasher.HashAsync(stream, cancellationToken: cancellationToken);
        return (both.Hash1, both.Hash2, live.FileInfo.Size);
    }

    private AbsolutePath LivePath(RelativePath gameFile)
        => installation.Locations.ToAbsolutePath(new GamePath(LocationId.Game, gameFile));

    /// <summary>
    /// The store key of the file's original: the hash it had when the game was first indexed, if that copy was backed
    /// up for this install and the store still holds it.
    /// </summary>
    private async ValueTask<Hash?> FindBackup(RelativePath gameFile)
    {
        if (installationId is not { } owner) return null;
        _initialState ??= ReadInitialState(owner);
        if (!_initialState.TryGetValue(new GamePath(LocationId.Game, gameFile), out var initial)) return null;
        if (!TryStoreKey(initial.Identity, initial.Size, out var original)) return null;

        var pinned = GameBackedUpFile.All(connection.Db)
            .Any(f => f.Hash == original && f.GameInstallId.Value == owner);
        if (!pinned || !await fileStore.HaveFile(original)) return null;
        return original;
    }

    /// <summary>
    /// The file store is keyed by xxHash3. An identity of another kind is joined to it through a relation the app
    /// computed from the bytes it archived, which is the one place two kinds may meet.
    /// </summary>
    private bool TryStoreKey(FileIdentity identity, Size size, out Hash key)
    {
        if (identity.TryXxHash3(out key)) return true;

        if (identity.Kind == HashKind.Sha1)
        {
            var sha1 = Sha1Value.From(identity.Digest);
            foreach (var db in fileHashes.Databases)
            {
                foreach (var relation in HashRelation.FindBySha1(db, sha1))
                {
                    if (relation.Size != size) continue;
                    key = relation.XxHash3;
                    return true;
                }
            }
        }

        key = default;
        return false;
    }

    private Dictionary<GamePath, (FileIdentity Identity, Size Size)> ReadInitialState(EntityId owner)
    {
        var result = new Dictionary<GamePath, (FileIdentity Identity, Size Size)>();
        var metadata = GameInstallMetadata.Load(connection.Db, owner);
        if (!GameInstallMetadata.InitialDiskStateTransaction.TryGetValue(metadata, out var initialTx)) return result;

        var asOf = connection.AsOf(TxId.From(initialTx.Value));
        var oldMetadata = GameInstallMetadata.Load(asOf, owner);
        foreach (var entry in DiskStateEntry.FindByGame(asOf, oldMetadata))
        {
            GamePath path = entry.Path;
            if (path.LocationId == LocationId.Game) result[path] = (entry.IdentityOf(), entry.Size);
        }
        return result;
    }

    private async ValueTask<Hash?> KnownXxHash64(Hash xxHash3)
    {
        var db = await fileHashes.GetFileHashesDb();
        foreach (var datom in db.Datoms(HashRelation.XxHash3, xxHash3))
        {
            var relation = HashRelation.Load(db, datom.E);
            if (relation.IsValid() && relation.Contains(HashRelation.XxHash64)) return relation.XxHash64;
        }
        return null;
    }
}
