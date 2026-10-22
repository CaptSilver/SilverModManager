using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Games.CreationEngine.Models;
using NexusMods.Games.CreationEngine.SortOrder;
using NexusMods.MnemonicDB.Abstractions;
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
}
