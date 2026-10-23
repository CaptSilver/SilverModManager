using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using NexusMods.Hashing.xxHash3;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using NexusMods.Paths;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Deployment.Vfs;

/// <summary>
/// Puts staged files in the file store and records them, and the names the game deleted, in the
/// loadout's Overrides group with the same rules the copy backend uses for changes it finds on disk.
/// </summary>
/// <remarks>
/// The mounted folder is the game's root location, so a mount-relative path becomes a path under
/// <see cref="LocationId.Game"/>.
/// </remarks>
public sealed class LoadoutPromotionSink : IPromotionSink
{
    private readonly IFileStore _fileStore;
    private readonly IConnection _connection;

    public LoadoutPromotionSink(IFileStore fileStore, IConnection connection)
    {
        _fileStore = fileStore;
        _connection = connection;
    }

    public async Task Backup(IReadOnlyList<(Hash Hash, Size Size, IStreamFactory Stream)> files, CancellationToken token = default)
    {
        var missing = new List<ArchivedFileEntry>();
        var seen = new HashSet<Hash>();
        foreach (var (hash, size, stream) in files)
        {
            if (!seen.Add(hash)) continue;
            if (await _fileStore.HaveFile(hash)) continue;
            missing.Add(new ArchivedFileEntry(stream, hash, size));
        }

        // The haves were filtered above and the caller verified the hashes, so the store has nothing to re-check.
        await _fileStore.BackupFiles(missing, deduplicate: false, token);
    }

    public async Task Record(LoadoutId loadoutId, IReadOnlyList<PromotedFile> files, IReadOnlyList<RelativePath> deleted, CancellationToken token = default)
    {
        var loadout = Loadout.Load(_connection.Db, loadoutId);
        if (!loadout.IsValid()) throw new InvalidOperationException($"Loadout {loadoutId} does not exist, so staged files cannot be recorded in it");

        var synchronizer = (ALoadoutSynchronizer)loadout.InstallationInstance.GetGame().Synchronizer;

        using var tx = _connection.BeginTransaction();
        var overrides = synchronizer.GetOrCreateOverridesGroup(tx, loadout);
        foreach (var file in files)
        {
            var path = new GamePath(LocationId.Game, file.Path);

            // The game recreated a name it had deleted. Writing a hash onto the DeletedFile row, as
            // UpsertFile would, leaves a row that is still a delete, so the file would never deploy.
            var existing = OverridesGroupItems.FindAtPath(loadout, overrides, path);
            if (existing.HasValue && existing.Value.TryGetAsDeletedFile(out _))
            {
                tx.Delete(existing.Value, recursive: false);
                OverridesGroupItems.AddFile(tx, loadout, overrides, path, file.Hash, file.Size);
                continue;
            }

            OverridesGroupItems.UpsertFile(tx, loadout, overrides, path, file.Hash, file.Size);
        }
        foreach (var path in deleted)
            OverridesGroupItems.AddReifiedDelete(tx, loadout, overrides, new GamePath(LocationId.Game, path));
        await tx.Commit();
    }
}
