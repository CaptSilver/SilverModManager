using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using NexusMods.Hashing.xxHash3;
using NexusMods.Hashing.xxHash3.Paths;
using NexusMods.Paths;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.DataModel.GameInstalled;

internal sealed class GameInstalledContentService : IGameInstalledContentService
{
    private readonly IConnection _connection;
    private readonly IFileStore _fileStore;

    public GameInstalledContentService(IServiceProvider serviceProvider)
    {
        _connection = serviceProvider.GetRequiredService<IConnection>();
        _fileStore = serviceProvider.GetRequiredService<IFileStore>();
    }

    public async Task<ClaimOutcome> Claim(GameInstalledItemId itemId, CancellationToken token = default)
    {
        var db = _connection.Db;
        var item = GameInstalledItem.Load(db, itemId);
        if (!item.IsValid()) throw new ArgumentException($"Game-installed item {itemId} does not exist", nameof(itemId));
        // Content the game re-downloads (a Creation) would come straight back and churn the claim.
        if (!item.CanBeClaimed) throw new InvalidOperationException($"Game-installed item `{item.SourceId}` cannot be claimed");

        var loadout = item.AsLoadoutItemGroup().AsLoadoutItem().Loadout;
        var synchronizer = SynchronizerOf(loadout);
        var files = item.AsLoadoutItemGroup().Children.OfTypeGameInstalledFile().ToArray();

        // The overrides row needs an archive like any ingested file, written before the commit so the row never
        // exists without one.
        // The recorded hash is from the last sync; the game may have rewritten the file since, and the archive must
        // hold the bytes the row will name. So the live file is hashed and its hash is the one used throughout.
        var live = new Dictionary<GamePath, (Hash Hash, Size Size)>();
        var backups = new List<ArchivedFileEntry>();
        var notBackedUp = new List<GamePath>();
        foreach (var file in files)
        {
            GamePath path = file.Path;
            var onDisk = loadout.InstallationInstance.Locations.ToAbsolutePath(path);
            if (!onDisk.FileExists) throw new InvalidOperationException($"`{path}` is no longer in the game folder");

            var hash = await onDisk.XxHash3Async(token: token);
            var size = onDisk.FileInfo.Size;
            live[path] = (hash, size);

            if (synchronizer.IsIgnoredBackupPath(path))
            {
                notBackedUp.Add(path);
                continue;
            }

            if (await _fileStore.HaveFile(hash)) continue;
            backups.Add(new ArchivedFileEntry(new NativeFileStreamFactory(onDisk), hash, size));
        }

        if (backups.Count > 0) await _fileStore.BackupFiles(backups, deduplicate: false, token);

        using var tx = _connection.BeginTransaction();
        var overrides = synchronizer.GetOrCreateOverridesGroup(tx, loadout);
        var moved = new List<GamePath>();
        foreach (var file in files)
        {
            GamePath path = file.Path;
            _ = new GameInstalledClaim.New(tx)
            {
                LoadoutId = loadout.LoadoutId,
                Path = path.ToGamePathParentTuple(loadout.Id),
                Source = item.Source,
                SourceId = item.SourceId,
                ClaimedAt = DateTimeOffset.UtcNow,
            };
            tx.Delete(file.Id, recursive: false);
            OverridesGroupItems.UpsertFile(tx, loadout, overrides, path, live[path].Hash, live[path].Size);
            moved.Add(path);
        }

        tx.Delete(item.Id, recursive: false);
        await tx.Commit();
        return new ClaimOutcome(moved, notBackedUp);
    }

    public async Task<UnclaimOutcome> Unclaim(GameInstalledClaimId claimId, CancellationToken token = default)
    {
        var db = _connection.Db;
        var claim = GameInstalledClaim.Load(db, claimId);
        if (!claim.IsValid()) throw new ArgumentException($"Claim {claimId} does not exist", nameof(claimId));

        var loadout = claim.Loadout;
        GamePath path = claim.Path;

        // The last indexed state can be older than the file, so the copy on disk is what gets compared.
        var onDisk = loadout.InstallationInstance.Locations.ToAbsolutePath(path);
        if (!onDisk.FileExists) return new UnclaimOutcome(false, $"`{path}` is no longer in the game folder");
        var diskHash = await onDisk.XxHash3Async(token: token);
        var diskSize = onDisk.FileInfo.Size;

        var row = FindOverridesFile(db, loadout, path);
        if (row is null) return new UnclaimOutcome(false, $"`{path}` is no longer in External Changes");
        if (row.Value.Hash != diskHash) return new UnclaimOutcome(false, $"`{path}` has been changed since it was claimed, the changed copy stays in External Changes");

        using var tx = _connection.BeginTransaction();
        tx.Delete(claim.Id, recursive: false);
        tx.Delete(row.Value.Id, recursive: false);

        var itemId = FindItem(db, loadout, claim.Source, claim.SourceId) ?? CreateItem(tx, db, loadout, claim.Source, claim.SourceId);
        _ = new GameInstalledFile.New(tx, out var fileId)
        {
            Path = path.ToGamePathParentTuple(loadout.Id),
            Hash = diskHash,
            Size = diskSize,
            LoadoutItem = new LoadoutItem.New(tx, fileId)
            {
                Name = path.FileName,
                LoadoutId = loadout.Id,
                ParentId = itemId,
            },
        };

        await tx.Commit();
        return new UnclaimOutcome(true, null);
    }

    private static ALoadoutSynchronizer SynchronizerOf(Loadout.ReadOnly loadout)
    {
        var synchronizer = loadout.InstallationInstance.GetGame().Synchronizer;
        return synchronizer as ALoadoutSynchronizer
               ?? throw new InvalidOperationException($"The synchronizer of {loadout.InstallationInstance.Game.DisplayName} does not derive from {nameof(ALoadoutSynchronizer)}");
    }

    private static LoadoutFile.ReadOnly? FindOverridesFile(IDb db, Loadout.ReadOnly loadout, GamePath path)
    {
        foreach (var group in LoadoutOverridesGroup.FindByOverridesFor(db, loadout.Id))
        {
            foreach (var row in group.AsLoadoutItemGroup().Children.OfTypeLoadoutItemWithTargetPath())
            {
                if ((GamePath)row.TargetPath != path) continue;
                return row.TryGetAsLoadoutFile(out var file) ? file : null;
            }
        }

        return null;
    }

    private static LoadoutItemGroupId? FindItem(IDb db, Loadout.ReadOnly loadout, string source, string sourceId)
    {
        foreach (var group in GameInstalledGroup.FindByInstalledFor(db, loadout.Id))
        {
            foreach (var item in group.AsLoadoutItemGroup().Children.OfTypeLoadoutItemGroup().OfTypeGameInstalledItem())
            {
                if (item.Source == source && item.SourceId == sourceId) return item.AsLoadoutItemGroup().LoadoutItemGroupId;
            }
        }

        return null;
    }

    /// <summary>
    /// Recreates an item the claim removed. The title and version are the recognizer's to fill in at the next
    /// reconcile, so the source id stands in for the name until then.
    /// </summary>
    private static LoadoutItemGroupId CreateItem(ITransaction tx, IDb db, Loadout.ReadOnly loadout, string source, string sourceId)
    {
        var existingGroup = GameInstalledGroup.FindByInstalledFor(db, loadout.Id).FirstOrDefault();
        var groupId = existingGroup.IsValid()
            ? existingGroup.AsLoadoutItemGroup().LoadoutItemGroupId
            : ALoadoutSynchronizer.CreateGameInstalledGroup(tx, loadout);

        _ = new GameInstalledItem.New(tx, out var itemId)
        {
            Source = source,
            SourceId = sourceId,
            Confidence = nameof(GameInstalledConfidence.GameRecord),
            CanBeClaimed = true,
            DepotShipped = false,
            LoadoutItemGroup = new LoadoutItemGroup.New(tx, itemId)
            {
                IsGroup = true,
                LoadoutItem = new LoadoutItem.New(tx, itemId)
                {
                    Name = sourceId,
                    LoadoutId = loadout.Id,
                    ParentId = groupId,
                },
            },
        };

        return LoadoutItemGroupId.From(itemId);
    }
}
