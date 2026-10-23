using DynamicData.Kernel;
using NexusMods.Hashing.xxHash3;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using NexusMods.Paths;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;

/// <summary>What <see cref="OverridesGroupItems.UpsertFile"/> did to the group.</summary>
public enum UpsertOutcome
{
    /// <summary>The group already had a file at the path; its hash and size were replaced.</summary>
    Updated,

    /// <summary>A new file was added to the group.</summary>
    Inserted,
}

/// <summary>
/// The rows a loadout's Overrides group gets for files that appeared in, or disappeared from, the
/// game folder outside the loadout. Shared by the copy backend's ingest and by anything else that
/// learns of such changes.
/// </summary>
/// <remarks>
/// The group id may come from <see cref="ALoadoutSynchronizer.GetOrCreateOverridesGroup"/> in the same
/// transaction, in which case it is a temporary id, the group has no children yet and the lookup of
/// existing rows is skipped.
/// </remarks>
public static class OverridesGroupItems
{
    /// <summary>
    /// The group's row at <paramref name="path"/>, if it has one. A group created in this transaction
    /// has no rows yet and is not looked up.
    /// </summary>
    public static Optional<LoadoutItemWithTargetPath.ReadOnly> FindAtPath(Loadout.ReadOnly loadout, LoadoutOverridesGroupId overrides, GamePath path)
    {
        if (overrides.Value.InPartition(PartitionId.Temp)) return Optional<LoadoutItemWithTargetPath.ReadOnly>.None;

        return LoadoutItemGroup.Load(loadout.Db, overrides.Value).Children
            .OfTypeLoadoutItemWithTargetPath()
            .FirstOrOptional(c => c.TargetPath == path);
    }

    /// <summary>
    /// Points the group's row at <paramref name="path"/> at the given content: writes the hash and
    /// size onto whatever row is already there, or adds a file.
    /// </summary>
    public static UpsertOutcome UpsertFile(ITransaction tx, Loadout.ReadOnly loadout, LoadoutOverridesGroupId overrides, GamePath path, Hash hash, Size size)
    {
        var existingRecord = FindAtPath(loadout, overrides, path);
        if (existingRecord.HasValue)
        {
            tx.Add(existingRecord.Value.Id, LoadoutFile.Hash, hash);
            tx.Add(existingRecord.Value.Id, LoadoutFile.Size, size);
            return UpsertOutcome.Updated;
        }

        AddFile(tx, loadout, overrides, path, hash, size);
        return UpsertOutcome.Inserted;
    }

    /// <summary>Adds a file to the group without looking for one already at <paramref name="path"/>.</summary>
    public static void AddFile(ITransaction tx, Loadout.ReadOnly loadout, LoadoutOverridesGroupId overrides, GamePath path, Hash hash, Size size)
    {
        var id = tx.TempId();
        var loadoutItem = new LoadoutItem.New(tx, id)
        {
            ParentId = overrides.Value,
            LoadoutId = loadout.Id,
            Name = path.FileName,
        };
        var loadoutItemWithTargetPath = new LoadoutItemWithTargetPath.New(tx, id)
        {
            LoadoutItem = loadoutItem,
            TargetPath = path.ToGamePathParentTuple(loadout.Id),
        };

        _ = new LoadoutFile.New(tx, id)
        {
            LoadoutItemWithTargetPath = loadoutItemWithTargetPath,
            Hash = hash,
            Size = size,
        };
    }

    /// <summary>
    /// Records that <paramref name="path"/> was deleted. A delete of a delete does nothing, and a
    /// delete over a file in the group removes that file instead of adding a delete, so whatever a
    /// lower item or the game folder has at the path wins again.
    /// </summary>
    public static void AddReifiedDelete(ITransaction tx, Loadout.ReadOnly loadout, LoadoutOverridesGroupId overrides, GamePath path)
    {
        var match = FindAtPath(loadout, overrides, path);
        if (match.HasValue)
        {
            // A delete of a delete does nothing
            if (match.Value.TryGetAsDeletedFile(out _))
                return;

            tx.Delete(match.Value, recursive: false);
            return;
        }

        _ = new DeletedFile.New(tx, out var id)
        {
            Reason = "Reified delete",
            LoadoutItemWithTargetPath = new LoadoutItemWithTargetPath.New(tx, id)
            {
                TargetPath = path.ToGamePathParentTuple(loadout.Id),
                LoadoutItem = new LoadoutItem.New(tx, id)
                {
                    Name = path.FileName,
                    ParentId = overrides.Value,
                    LoadoutId = loadout.Id,
                },
            },
        };
    }
}
