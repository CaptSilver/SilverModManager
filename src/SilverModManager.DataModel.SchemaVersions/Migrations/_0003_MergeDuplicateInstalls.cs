using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Deployment;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.DataModel.SchemaVersions;

/// <summary>
/// Folds installation rows that name the same folder of the same game and store into the oldest of them.
/// </summary>
/// <remarks>
/// Adding a game from two places at once could commit a row per caller. Lookups return the oldest row, so a
/// rescan always wrote that row's disk state, while a sync of a loadout on a younger row wrote the younger one's.
/// The oldest row's entry wins for a path both rows track. A younger row's entry for a path the oldest lacks is
/// moved over: it records a file that row's apply deployed, and a protected path is only re-indexed when it has an
/// entry, so dropping it would leave that file untracked. Plan entries are merged root by root, the row applied
/// last winning, because the files on disk are the ones that apply deployed. Everything else that points at a
/// younger row is pointed at the oldest one.
///
/// A link apply of a younger row's loadout staged its files under that row's id, so <see cref="Prepare"/> hands
/// them to the oldest row in the staging store before the rows go. It runs outside the migration's transaction
/// and can't be rolled back, so it only adds: the files are hard linked across and the younger directory becomes a
/// link to the oldest one's. If the transaction then fails, every link still resolves and the next start runs
/// both again, which the store's merge finishes without redoing.
///
/// Older Release builds could leave a disk state entry with its path but without its hash, size or time, when a
/// retraction named a path value the entry no longer held. Reading one throws, so those are deleted here too.
///
/// Rows are grouped by the stored path as spelled. Every duplicate was written from the same install, so the
/// spellings match; resolving symlinks would make the result depend on what is mounted when the migration runs.
/// </remarks>
// ReSharper disable once InconsistentNaming
public sealed class _0003_MergeDuplicateInstalls(ILogger<_0003_MergeDuplicateInstalls> logger, ILooseStagingStore stagingStore) : ITransactionalMigration
{
    public static (MigrationId Id, string Name) IdAndName => MigrationId.ParseNameAndId(nameof(_0003_MergeDuplicateInstalls));

    public Task Prepare(IDb db)
    {
        foreach (var (canonical, duplicates) in DuplicateGroups(db))
        {
            foreach (var duplicate in duplicates)
                stagingStore.MergeInstallation(duplicate.GameInstallMetadataId, canonical.GameInstallMetadataId);
        }
        return Task.CompletedTask;
    }

    public void Migrate(ITransaction tx, IDb db)
    {
        var halfWritten = DeleteHalfWrittenDiskState(tx, db);

        foreach (var (canonical, duplicates) in DuplicateGroups(db))
        {
            logger.LogInformation("Merging {Count} installation rows for `{Path}` ({Game}, {Store}) into {Canonical}", duplicates.Length + 1, canonical.Path, canonical.Game.Value, canonical.Store, canonical.Id);
            Merge(tx, db, canonical, duplicates, halfWritten);
        }
    }

    // The oldest row of each group, and the rest.
    private static IEnumerable<(GameInstallMetadata.ReadOnly Canonical, GameInstallMetadata.ReadOnly[] Duplicates)> DuplicateGroups(IDb db)
        => GameInstallMetadata.All(db)
            .Where(row => GameInstallMetadata.Game.Contains(row))
            .GroupBy(row => (row.Game.Value, row.Store, row.Path))
            .Where(group => group.Count() > 1)
            .Select(group => group.OrderBy(row => row.Id.Value).ToArray())
            .Select(rows => (rows[0], rows[1..]));

    private HashSet<EntityId> DeleteHalfWrittenDiskState(ITransaction tx, IDb db)
    {
        var candidates = db.Datoms(DiskStateEntry.Path).Select(d => d.E)
            .Concat(db.Datoms(DiskStateEntry.Game).Select(d => d.E))
            .ToHashSet();

        var deleted = new HashSet<EntityId>();
        foreach (var id in candidates)
        {
            var entry = DiskStateEntry.Load(db, id);
            if (DiskStateEntry.Path.Contains(entry) && DiskStateEntry.Game.Contains(entry)
                && (DiskStateEntry.XxHash3.Contains(entry) || DiskStateEntry.Identity.Contains(entry))
                && DiskStateEntry.Size.Contains(entry) && DiskStateEntry.LastModified.Contains(entry)) continue;

            var path = DiskStateEntry.Path.TryGetValue(entry, out var p) ? $"{p.Item2}/{p.Item3}" : "(no path)";
            logger.LogInformation("Deleting disk state entry {Entry} for `{Path}`: an earlier build left it without its hash, size or time", id, path);
            tx.Delete(id, recursive: false);
            deleted.Add(id);
        }
        return deleted;
    }

    private void Merge(ITransaction tx, IDb db, GameInstallMetadata.ReadOnly canonical, GameInstallMetadata.ReadOnly[] duplicates, HashSet<EntityId> halfWritten)
    {
        GameInstallMetadata.ReadOnly[] byLastApply = [..new[] { canonical }.Concat(duplicates)
            .OrderByDescending(row => GameInstallMetadata.LastSyncedLoadoutTransaction.TryGetValue(row, out var txId) ? txId.Value : 0UL)
            .ThenBy(row => row.Id.Value)];

        var lastApplied = CarryLastApply(tx, canonical, byLastApply);
        MergePlans(tx, db, canonical, byLastApply);
        MergeDiskState(tx, db, canonical, byLastApply, halfWritten);
        RenameClashingLoadouts(tx, db, canonical, duplicates, lastApplied);

        var diskStateGame = db.AttributeCache.GetAttributeId(DiskStateEntry.Game.Id);
        var planInstallation = db.AttributeCache.GetAttributeId(DeploymentPlanEntry.Installation.Id);
        Span<byte> canonicalValue = stackalloc byte[sizeof(ulong)];
        var canonicalId = canonical.Id;
        MemoryMarshal.Write(canonicalValue, in canonicalId);

        foreach (var duplicate in duplicates)
        {
            foreach (var reference in db.ReferencesTo(duplicate.Id))
            {
                // Disk state and plan rows were handled above: their paths carry the row id too.
                if (reference.A == diskStateGame || reference.A == planInstallation) continue;
                if (reference.Prefix.ValueTag != ValueTag.Reference) continue;

                if (db.AttributeCache.IsCardinalityMany(reference.A)) tx.Add(reference.Retract());
                tx.Add(reference.E, reference.A, ValueTag.Reference, canonicalValue);
            }

            tx.Delete(duplicate.Id, recursive: false);
        }
    }

    /// <summary>
    /// One entry per path survives, on the oldest row: the oldest row's own, else the one from the row applied
    /// most recently. The rest are deleted.
    /// </summary>
    private static void MergeDiskState(ITransaction tx, IDb db, GameInstallMetadata.ReadOnly canonical, GameInstallMetadata.ReadOnly[] byLastApply, HashSet<EntityId> halfWritten)
    {
        var tracked = new HashSet<(LocationId, RelativePath)>();
        foreach (var row in byLastApply.Where(r => r.Id == canonical.Id).Concat(byLastApply.Where(r => r.Id != canonical.Id)))
        {
            foreach (var entry in DiskStateEntry.FindByGame(db, row))
            {
                if (halfWritten.Contains(entry.Id)) continue;

                var (_, location, path) = entry.Path;
                if (!tracked.Add((location, path)))
                {
                    if (row.Id != canonical.Id) tx.Delete(entry.Id, recursive: false);
                    continue;
                }

                if (row.Id == canonical.Id) continue;
                tx.Add(entry.Id, DiskStateEntry.Path, (canonical.Id, location, path));
                tx.Add(entry.Id, DiskStateEntry.Game, canonical.Id);
            }
        }
    }

    // Returns the loadout the folder was last applied with, if any row recorded one.
    private static EntityId? CarryLastApply(ITransaction tx, GameInstallMetadata.ReadOnly canonical, GameInstallMetadata.ReadOnly[] byLastApply)
    {
        var last = byLastApply.FirstOrDefault(row => GameInstallMetadata.LastSyncedLoadout.Contains(row));
        if (!last.IsValid()) return null;

        var loadout = GameInstallMetadata.LastSyncedLoadout.Get(last);
        if (last.Id == canonical.Id) return loadout;

        tx.Add(canonical.Id, GameInstallMetadata.LastSyncedLoadout, loadout);
        if (GameInstallMetadata.LastSyncedLoadoutTransaction.TryGetValue(last, out var txId))
            tx.Add(canonical.Id, GameInstallMetadata.LastSyncedLoadoutTransaction, txId);
        return loadout;
    }

    private void MergePlans(ITransaction tx, IDb db, GameInstallMetadata.ReadOnly canonical, GameInstallMetadata.ReadOnly[] byLastApply)
    {
        var winners = new Dictionary<(LocationId, RelativePath), DeploymentPlanEntry.ReadOnly>();
        foreach (var row in byLastApply)
        {
            foreach (var entry in DeploymentPlanEntry.FindByInstallation(db, row))
            {
                var (_, location, path) = entry.Root;
                if (winners.TryGetValue((location, path), out var winner))
                {
                    if (winner.Backend != entry.Backend)
                        logger.LogInformation("Root `{Location}/{Path}` keeps backend `{Kept}` from the last apply; dropping `{Dropped}`", location, path, winner.Backend, entry.Backend);
                    tx.Delete(entry.Id, recursive: false);
                    continue;
                }

                winners[(location, path)] = entry;
                if (row.Id == canonical.Id) continue;
                tx.Add(entry.Id, DeploymentPlanEntry.Installation, canonical.Id);
                tx.Add(entry.Id, DeploymentPlanEntry.Root, (canonical.Id, location, path));
            }
        }
    }

    /// <summary>
    /// Each racing CreateLoadout picked its loadout's names from its own row, so loadouts that end up on one row
    /// can share a short name, which is how the command line tells them apart. The loadout the folder was last
    /// applied with keeps its names, since that is the one a launch by name means; then the oldest row's loadouts,
    /// then the rest in id order.
    /// </summary>
    private static void RenameClashingLoadouts(ITransaction tx, IDb db, GameInstallMetadata.ReadOnly canonical, GameInstallMetadata.ReadOnly[] duplicates, EntityId? lastApplied)
    {
        var all = new[] { canonical }.Concat(duplicates)
            .SelectMany(row => Loadout.FindByInstallation(db, row).Select(l => (Loadout: l, Canonical: row.Id == canonical.Id)))
            .Where(x => x.Loadout.IsVisible())
            .OrderByDescending(x => x.Loadout.Id == lastApplied)
            .ThenByDescending(x => x.Canonical)
            .ThenBy(x => x.Loadout.Id.Value)
            .Select(x => x.Loadout);

        var shortNames = new List<string>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var loadout in all)
        {
            var shortName = loadout.ShortName;
            if (shortNames.Contains(shortName))
            {
                shortName = LoadoutNameProvider.GetNewShortName(CollectionsMarshal.AsSpan(shortNames));
                tx.Add(loadout.Id, Loadout.ShortName, shortName);
            }
            shortNames.Add(shortName);

            var name = loadout.Name.Trim();
            var unique = name;
            for (var i = 2; names.Contains(unique); i++) unique = $"{name} ({i})";
            if (unique != loadout.Name) tx.Add(loadout.Id, Loadout.Name, unique);
            names.Add(unique);
        }
    }
}
