using Microsoft.Extensions.Logging;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.GC;
using SilverModManager.Abstractions.Games.FileHashes;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.DataModel;

/// <summary>What the backups of one game's own files take up, and how much of it nothing needs.</summary>
/// <param name="Total">Bytes of every backup the game has, as the store holds them.</param>
/// <param name="Count">How many backups.</param>
/// <param name="Removable">Bytes of the backups no loadout needs to put the game folder back.</param>
/// <param name="RemovableCount">How many of those.</param>
public sealed record BackupSpaceReport(Size Total, int Count, Size Removable, int RemovableCount);

/// <summary>
/// Measures the backups of a game's own files (the <see cref="GameBackedUpFile"/> roots that keep them out of the
/// garbage collector) and drops the ones nothing relies on. A backup is relied on when a loadout of the game names
/// its bytes, or when the disk state the game folder was first indexed in or is in now does: putting the folder
/// back reads those. The game files the loadout's game version lists count as named by the loadout: the sync
/// restores them from the store, and after a game update a backup of one that a mod covers is named nowhere else.
/// The sync never takes a backup away by itself, so without this they only ever grow.
/// </summary>
public sealed class GameBackupSpace(
    IConnection connection,
    IFileStore fileStore,
    IFileIdentityResolver identityResolver,
    IFileHashesService fileHashes,
    IGameRegistry gameRegistry,
    IGarbageCollectorRunner garbageCollector,
    ILogger<GameBackupSpace> logger)
{
    /// <summary>Measures the backups of the install; nothing is changed.</summary>
    public async Task<BackupSpaceReport> Measure(GameInstallMetadataId install)
    {
        var db = connection.Db;
        return Classify(db, install, await SizeBackups(db, install)).Report;
    }

    /// <summary>
    /// <see cref="Measure"/>, or null when the backups take up no more than <paramref name="limit"/>. The health
    /// check runs this on every loadout change, and working out what is needed walks every loadout item and disk
    /// state entry of the game, so it is skipped while the total is under the limit.
    /// </summary>
    public async Task<BackupSpaceReport?> MeasureIfOver(GameInstallMetadataId install, Size limit)
    {
        var db = connection.Db;
        var backups = await SizeBackups(db, install);
        return backups.Total <= limit ? null : Classify(db, install, backups).Report;
    }

    /// <summary>
    /// Removes the roots of the backups nothing relies on, then collects the garbage so their bytes leave the store.
    /// Returns what was removed in <see cref="BackupSpaceReport.Removable"/>.
    /// </summary>
    public async Task<BackupSpaceReport> RemoveUnneeded(GameInstallMetadataId install)
    {
        var db = connection.Db;
        var scan = Classify(db, install, await SizeBackups(db, install));
        if (scan.Unneeded.Count == 0) return scan.Report;

        using var tx = connection.BeginTransaction();
        foreach (var file in scan.Unneeded)
            tx.Delete(file, recursive: false);
        await tx.Commit();

        logger.LogInformation("Removed {Count} unneeded game file backups ({Size})", scan.Report.RemovableCount, scan.Report.Removable);
        await garbageCollector.RunAsync();
        return scan.Report;
    }

    private async Task<SizedBackups> SizeBackups(IDb db, GameInstallMetadataId install)
    {
        // Two rows can root the same bytes; the store holds them once.
        var backups = new List<SizedBackup>();
        var total = Size.Zero;
        foreach (var rows in GameBackedUpFile.All(db).Where(f => f.GameInstallId == install).GroupBy(f => f.Hash))
        {
            var size = await fileStore.SizeOf(rows.First().Identity) ?? Size.Zero;
            backups.Add(new SizedBackup(rows.Key, rows.ToList(), size));
            total += size;
        }
        return new SizedBackups(backups, total);
    }

    private ScanResult Classify(IDb db, GameInstallMetadataId install, SizedBackups backups)
    {
        var removable = Size.Zero;
        var removableCount = 0;
        var unneeded = new List<GameBackedUpFile.ReadOnly>();
        if (NeededHashes(db, GameInstallMetadata.Load(db, install)) is not { } needed)
            return new ScanResult(new BackupSpaceReport(backups.Total, backups.Items.Count, removable, removableCount), unneeded);

        foreach (var backup in backups.Items)
        {
            if (needed.Contains(backup.Hash)) continue;
            removableCount++;
            removable += backup.Size;
            unneeded.AddRange(backup.Rows);
        }

        return new ScanResult(new BackupSpaceReport(backups.Total, backups.Items.Count, removable, removableCount), unneeded);
    }

    /// <summary>
    /// The store keys something still restores from, or null when that cannot be told: a loadout whose installation
    /// is not found has a game file list nobody can read, and any backup might be on it.
    /// </summary>
    private HashSet<Hash>? NeededHashes(IDb db, GameInstallMetadata.ReadOnly metadata)
    {
        var needed = new HashSet<Hash>();

        foreach (var loadout in Loadout.FindByInstallation(db, metadata))
        {
            // The vanilla state loadout counts too: its whole job is putting the game's own files back.
            if (loadout.LoadoutKind == LoadoutKind.Deleted) continue;

            if (!gameRegistry.TryGetGameInstallation(loadout, out var installation))
            {
                logger.LogWarning("Keeping every game file backup of {Install}: the installation of loadout {Loadout} is not found, so its game file list cannot be read", metadata.Id, loadout.Name);
                return null;
            }

            foreach (var gameFile in fileHashes.GetGameFiles(installation, loadout.LocatorIds))
                AddKeysOf(needed, gameFile.Identity, gameFile.Size);

            foreach (var item in LoadoutItem.FindByLoadout(db, loadout))
            {
                if (item.TryGetAsLoadoutItemWithTargetPath(out var withPath) && withPath.TryGetAsLoadoutFile(out var file))
                    needed.Add(file.Hash);
            }
        }

        foreach (var entry in DiskStateEntry.FindByGame(db, metadata))
            AddKeysOf(needed, entry.IdentityOf(), entry.Size);

        if (GameInstallMetadata.InitialDiskStateTransaction.TryGetValue(metadata, out var initialTx))
        {
            var asOf = connection.AsOf(TxId.From(initialTx.Value));
            foreach (var entry in DiskStateEntry.FindByGame(asOf, GameInstallMetadata.Load(asOf, metadata.Id)))
                AddKeysOf(needed, entry.IdentityOf(), entry.Size);
        }

        return needed;
    }

    private void AddKeysOf(HashSet<Hash> needed, FileIdentity identity, Size size)
    {
        foreach (var key in identityResolver.StoreKeyCandidates(identity, size))
            needed.Add(key.XxHash3());
    }

    private sealed record SizedBackup(Hash Hash, List<GameBackedUpFile.ReadOnly> Rows, Size Size);

    private sealed record SizedBackups(List<SizedBackup> Items, Size Total);

    private sealed record ScanResult(BackupSpaceReport Report, List<GameBackedUpFile.ReadOnly> Unneeded);
}
