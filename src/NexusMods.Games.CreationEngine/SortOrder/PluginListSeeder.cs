using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Games.Bethesda.LoadOrder;
using NexusMods.Games.CreationEngine.Abstractions;
using NexusMods.Games.CreationEngine.Models;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Games.CreationEngine.SortOrder;

/// <summary>
/// Fills a loadout's plugin order once, from the plugin list the game already has on disk, so the first Apply
/// reproduces what the user was running instead of replacing it with a guess.
/// </summary>
internal static class PluginListSeeder
{
    /// <summary>Whether <paramref name="sortOrderId"/> has been seeded.</summary>
    public static bool IsSeeded(IDb db, SortOrderId sortOrderId) => PluginSortOrder.Load(db, sortOrderId.Value).SeededFromGameFile;

    /// <summary>
    /// Stages the order and switches the game's list gives, or the resolver's default order when the game has no
    /// list yet, and marks the order as seeded, all in one transaction.
    /// </summary>
    /// <remarks>
    /// A plugin the game's list does not mention keeps the row it has (switched on) and follows the listed ones:
    /// a mod installed in the app before the first Apply is meant to be loaded. A listed name the loadout does not
    /// have yet still gets its row, kept hidden until the plugin arrives: on a fresh manage the seed runs before
    /// the plugins already in the Data folder are taken into the loadout, and they must keep the list's order and
    /// switches when they are.
    /// </remarks>
    public static async Task Seed(
        ICreationEngineGame game,
        IPluginSortOrderVariety variety,
        IConnection connection,
        Loadout.ReadOnly loadout,
        SortOrderId sortOrderId)
    {
        var rows = variety.GetSortOrderItems(sortOrderId, loadout.Db)
            .OfType<PluginReactiveSortItem>()
            .OrderBy(item => item.SortIndex)
            .ToList();
        var state = await game.PluginList(loadout.InstallationInstance).ReadGameState(game, loadout);

        using var tx = connection.BeginTransaction();
        if (state is null)
        {
            var defaults = PluginDefaultOrder.Sort(rows.Select(row => row.LoadoutData as PluginLoadoutData
                ?? throw new InvalidOperationException($"Plugin '{row.DisplayName}' is listed without its plugin data.")).ToList());
            variety.StageOrder(sortOrderId, defaults.Select(data => data.Name).ToList(), tx);
        }
        else
        {
            var early = game.EarlyLoadingPlugins(loadout.InstallationInstance).ToHashSet(PluginNameComparer.Instance);
            var rowsByKey = rows.ToDictionary(row => row.Key);
            var seen = new HashSet<SortItemKey<string>>();
            var listed = new List<(string Name, bool? IsEnabled)>();
            foreach (var (name, active) in state.Entries)
            {
                var key = PluginSortData.KeyFor(name);
                if (!seen.Add(key)) continue;

                // The early loaders stay in the locked block at the top, whatever the file says about them.
                if (rowsByKey.TryGetValue(key, out var row))
                {
                    if (!row.IsLocked) listed.Add((row.DisplayName, active));
                }
                else if (!early.Contains(name))
                {
                    listed.Add((name, active));
                }
            }

            var order = rows.Where(row => row.IsLocked).Select(row => (row.DisplayName, (bool?)null))
                .Concat(listed)
                .Concat(rows.Where(row => !row.IsLocked && !seen.Contains(row.Key)).Select(row => (row.DisplayName, (bool?)null)))
                .ToList();
            variety.StageOrder(sortOrderId, order, tx);
        }

        tx.Add(sortOrderId.Value, PluginSortOrder.SeededFromGameFile, true);
        await tx.Commit();
    }
}
