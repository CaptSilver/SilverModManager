using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts;
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
/// The folder's disk state is the oldest row's, so the younger rows' copies are dropped rather than merged; the
/// next rescan fills in anything they knew. Their plan entries are merged root by root, the row applied last
/// winning, because the files on disk are the ones that apply deployed. Everything else that points at a younger
/// row is pointed at the oldest one.
///
/// Rows are grouped by the stored path as spelled. Every duplicate was written from the same install, so the
/// spellings match; resolving symlinks would make the result depend on what is mounted when the migration runs.
/// </remarks>
// ReSharper disable once InconsistentNaming
public sealed class _0003_MergeDuplicateInstalls(ILogger<_0003_MergeDuplicateInstalls> logger) : ITransactionalMigration
{
    public static (MigrationId Id, string Name) IdAndName => MigrationId.ParseNameAndId(nameof(_0003_MergeDuplicateInstalls));

    public Task Prepare(IDb db) => Task.CompletedTask;

    public void Migrate(ITransaction tx, IDb db)
    {
        var groups = GameInstallMetadata.All(db)
            .Where(row => GameInstallMetadata.Game.Contains(row))
            .GroupBy(row => (row.Game.Value, row.Store, row.Path))
            .Where(group => group.Count() > 1);

        foreach (var group in groups)
        {
            var rows = group.OrderBy(row => row.Id.Value).ToArray();
            logger.LogInformation("Merging {Count} installation rows for `{Path}` ({Game}, {Store}) into {Canonical}", rows.Length, group.Key.Path, group.Key.Value, group.Key.Store, rows[0].Id);
            Merge(tx, db, rows[0], rows[1..]);
        }
    }

    private void Merge(ITransaction tx, IDb db, GameInstallMetadata.ReadOnly canonical, GameInstallMetadata.ReadOnly[] duplicates)
    {
        GameInstallMetadata.ReadOnly[] byLastApply = [..new[] { canonical }.Concat(duplicates)
            .OrderByDescending(row => GameInstallMetadata.LastSyncedLoadoutTransaction.TryGetValue(row, out var txId) ? txId.Value : 0UL)
            .ThenBy(row => row.Id.Value)];

        CarryLastApply(tx, canonical, byLastApply);
        MergePlans(tx, db, canonical, byLastApply);
        RenameClashingLoadouts(tx, db, canonical, duplicates);

        var diskStateGame = db.AttributeCache.GetAttributeId(DiskStateEntry.Game.Id);
        var planInstallation = db.AttributeCache.GetAttributeId(DeploymentPlanEntry.Installation.Id);
        Span<byte> canonicalValue = stackalloc byte[sizeof(ulong)];
        var canonicalId = canonical.Id;
        MemoryMarshal.Write(canonicalValue, in canonicalId);

        foreach (var duplicate in duplicates)
        {
            foreach (var entry in DiskStateEntry.FindByGame(db, duplicate))
                tx.Delete(entry.Id, recursive: false);

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

    private static void CarryLastApply(ITransaction tx, GameInstallMetadata.ReadOnly canonical, GameInstallMetadata.ReadOnly[] byLastApply)
    {
        var last = byLastApply.FirstOrDefault(row => GameInstallMetadata.LastSyncedLoadout.Contains(row));
        if (!last.IsValid() || last.Id == canonical.Id) return;

        tx.Add(canonical.Id, GameInstallMetadata.LastSyncedLoadout, GameInstallMetadata.LastSyncedLoadout.Get(last));
        if (GameInstallMetadata.LastSyncedLoadoutTransaction.TryGetValue(last, out var txId))
            tx.Add(canonical.Id, GameInstallMetadata.LastSyncedLoadoutTransaction, txId);
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
    /// can share a short name, which is how the command line tells them apart.
    /// </summary>
    private static void RenameClashingLoadouts(ITransaction tx, IDb db, GameInstallMetadata.ReadOnly canonical, GameInstallMetadata.ReadOnly[] duplicates)
    {
        var kept = Loadout.FindByInstallation(db, canonical).Where(l => l.IsVisible()).ToArray();
        var shortNames = kept.Select(l => l.ShortName).ToList();
        var names = kept.Select(l => l.Name.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var loadout in duplicates.SelectMany(row => Loadout.FindByInstallation(db, row)).Where(l => l.IsVisible()).OrderBy(l => l.Id.Value))
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
