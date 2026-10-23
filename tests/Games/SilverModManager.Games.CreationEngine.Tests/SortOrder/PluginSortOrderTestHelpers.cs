using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Games.CreationEngine.Models;
using NexusMods.Games.CreationEngine.SortOrder;
using NexusMods.Hashing.xxHash3;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Hashes;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Games.CreationEngine.Tests.SortOrder;

internal static class PluginSortOrderTestHelpers
{
    public static IPluginSortOrderVariety PluginVariety(this IGame game) =>
        game.SortOrderManager.GetSortOrderVarieties().OfType<IPluginSortOrderVariety>().Single();

    public static SortOrderId SortOrderOf(this IPluginSortOrderVariety variety, Loadout.ReadOnly loadout, IConnection connection) =>
        variety.GetSortOrderIdFor(loadout.LoadoutId, connection.Db).Value;

    public static SortItemKey<string> Key(string name) => new(name.ToLowerInvariant());

    /// <summary>
    /// Creating a loadout already syncs it once, which seeds from whatever the game has then. This puts a loadout
    /// back in the state of one made before the seed existed, with its plugins installed and the order not filled.
    /// </summary>
    public static async Task Unseed(this IPluginSortOrderVariety variety, Loadout.ReadOnly loadout, IConnection connection)
    {
        var sortOrderId = variety.SortOrderOf(loadout, connection);
        await variety.ReconcileSortOrder(sortOrderId);
        using var tx = connection.BeginTransaction();
        tx.Add(sortOrderId.Value, PluginSortOrder.SeededFromGameFile, false);
        await tx.Commit();
    }

    /// <summary>
    /// Records a plugin as a file the game installed itself, under one item of the "Installed by the game" group, the
    /// way a sync of game-installed content stores it. Returns the group, so a test can switch it off.
    /// </summary>
    public static async Task<(EntityId Group, EntityId Item)> AddGameInstalledPlugin(
        this IConnection connection, Loadout.ReadOnly loadout, string title, string path, ulong hash = 0xAB)
    {
        using var tx = connection.BeginTransaction();
        _ = new GameInstalledGroup.New(tx, out var groupId)
        {
            InstalledForId = loadout,
            LoadoutItemGroup = new LoadoutItemGroup.New(tx, groupId)
            {
                IsGroup = true,
                LoadoutItem = new LoadoutItem.New(tx, groupId)
                {
                    Name = "Installed by the game",
                    LoadoutId = loadout.Id,
                },
            },
        };
        _ = new GameInstalledItem.New(tx, out var itemId)
        {
            Source = "SkyrimCreation",
            SourceId = path,
            Confidence = "GameRecord",
            CanBeClaimed = true,
            DepotShipped = false,
            LoadoutItemGroup = new LoadoutItemGroup.New(tx, itemId)
            {
                IsGroup = true,
                LoadoutItem = new LoadoutItem.New(tx, itemId)
                {
                    Name = title,
                    LoadoutId = loadout.Id,
                    ParentId = groupId,
                },
            },
        };
        _ = new GameInstalledFile.New(tx, out var fileId)
        {
            Path = new GamePath(LocationId.Game, path).ToGamePathParentTuple(loadout.Id),
            Hash = Hash.From(hash),
            Size = Size.From(10),
            LoadoutItem = new LoadoutItem.New(tx, fileId)
            {
                Name = path,
                LoadoutId = loadout.Id,
                ParentId = itemId,
            },
        };
        var result = await tx.Commit();
        return (result[groupId], result[itemId]);
    }
}
