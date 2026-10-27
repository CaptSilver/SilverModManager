using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Games.LoadOrder;

/// <summary>
/// Writes the order into the loadout's priority rows and switches groups on or off as the order says.
/// </summary>
public sealed class DefaultPriorityOrderSink(ILoadoutManager loadouts, ILogger<DefaultPriorityOrderSink> logger) : IModOrderSink
{
    /// <summary>The tab title in SortingSelectionView.axaml.</summary>
    private const string FileConflictsPage = "File Conflicts";

    public string FixPage(IGame game) => PriorityPage(game);

    /// <summary>
    /// Where the priority rows are edited: the Load Order tab exists only for a game with no sort order of its own
    /// (SortingSelectionViewModel), so any other game edits them on File Conflicts.
    /// </summary>
    public static string PriorityPage(IGame game) =>
        game.SortOrderManager.GetSortOrderVarieties().Count == 0 ? ModPriorityOrderVariety.PageName : FileConflictsPage;

    public IReadOnlyList<ModOrderDrop> Apply(Loadout.ReadOnly loadout, ModOrder order, ModOrderSource source, ITransaction tx)
    {
        var result = loadouts.SetGroupOrder(loadout.LoadoutId, [..order.WinnerToLoser.Select(static entry => entry.Group)], tx);

        foreach (var entry in order.WinnerToLoser)
        {
            if (entry.Enabled is not { } enabled) continue;

            var item = LoadoutItem.Load(loadout.Db, entry.Group);
            if (!enabled && !item.IsDisabled) tx.Add(item.Id, LoadoutItem.Disabled, Null.Instance);
            else if (enabled && item.IsDisabled) tx.Retract(item.Id, LoadoutItem.Disabled, Null.Instance);
        }

        if (source.SeedVarieties) SeedVarieties(loadout, result.Resolved, tx);

        logger.LogTrace("Ordered {Count} groups of '{Source}'; {Overridden} pairs were turned round by stored rules", order.WinnerToLoser.Count, source.Name, result.Overridden.Count);

        return
        [
            ..result.Overridden.Select(overridden => new ModOrderDrop(
                $"{Name(loadout, overridden.RequestedWinner)} above {Name(loadout, overridden.RequestedLoser)}",
                overridden.Displaced
                    ? $"the order moved to make room for the stored rule '{GroupRuleText.WinsOver(overridden.Rule)}'"
                    : $"the stored rule '{GroupRuleText.WinsOver(overridden.Rule)}' puts them the other way round")),
        ];
    }

    /// <summary>
    /// Seeds each of the game's varieties that has a sort order for the loadout, so a plugin, pak or REDmod list follows
    /// the mod order the same way the priority rows do. A variety without a stored order yet is left to its first reconcile.
    /// </summary>
    private void SeedVarieties(Loadout.ReadOnly loadout, IReadOnlyList<LoadoutItemGroupId> winnerToLoser, ITransaction tx)
    {
        foreach (var variety in loadout.InstallationInstance.GetGame().SortOrderManager.GetSortOrderVarieties())
        {
            if (variety is not IGroupOrderSeedable seedable) continue;

            var sortOrderId = variety.GetSortOrderIdFor(loadout.LoadoutId, loadout.Db);
            if (!sortOrderId.HasValue)
            {
                logger.LogTrace("Variety {Variety} has no sort order for the loadout yet; not seeding it", variety.SortOrderVarietyId);
                continue;
            }

            seedable.SeedFromGroupOrder(sortOrderId.Value, winnerToLoser, tx, loadout.Db);
        }
    }

    private static string Name(Loadout.ReadOnly loadout, LoadoutItemGroupId group) => LoadoutItem.Load(loadout.Db, group).Name;
}
