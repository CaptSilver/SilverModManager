using NexusMods.Hashing.xxHash3;
using NexusMods.HyperDuck;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Abstractions.Loadouts.Synchronizers;

public partial class ALoadoutSynchronizer
{
    private static Query<(EntityId Id, Hash Hash, Size Size, LocationId Location, RelativePath Path, string ItemType)> WinningFilesQuery(IDb db, LoadoutId loadoutId)
    {
        // TODO: https://github.com/Nexus-Mods/NexusMods.MnemonicDB/issues/183
        return db.Connection.Query<(EntityId Id, Hash Hash, Size Size, LocationId LocationId, RelativePath Path, string ItemType)>(
            $"SELECT Id, Hash, Size, Path.Location, Path.Path, ItemType::VARCHAR FROM synchronizer.WinningFiles({db}) WHERE Loadout = {loadoutId} ORDER BY ItemType"
        );
    }

    /// <summary>
    /// The paths whose winning loadout item is a delete. <see cref="BuildSyncTree{T}"/> leaves these out of
    /// the tree (and a winning delete also replaces the game file under it), so the tree alone cannot say
    /// which paths the loadout wants gone. Both read the same query, so they cannot disagree about who won.
    /// </summary>
    public IReadOnlyList<GamePath> DeletedWinners(Loadout.ReadOnly loadout)
    {
        return WinningFilesQuery(Connection.Db, loadout)
            .Where(static row => row.ItemType == "Deleted")
            .Select(static row => new GamePath(row.Location, row.Path))
            .ToArray();
    }

    // TODO: https://github.com/Nexus-Mods/NexusMods.MnemonicDB/issues/183
    private static LoadoutSourceItemType ToItemType(string value) => value switch
    {
        "Loadout" => LoadoutSourceItemType.Loadout,
        "Game" => LoadoutSourceItemType.Game,
        "Deleted" => LoadoutSourceItemType.Deleted,
        "Intrinsic" => LoadoutSourceItemType.Intrinsic,
        _ => throw new ArgumentException($"Unknown item type: `{value}`", nameof(value)),
    };
}
