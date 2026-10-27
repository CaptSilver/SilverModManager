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
        var result = loadouts.SetGroupOrder(loadout.LoadoutId, RequestedOrder(loadout, order, source), tx, source.NewGroups);

        foreach (var entry in order.WinnerToLoser)
        {
            if (entry.Enabled is not { } enabled) continue;
            if (source.NewGroups is { } added && !added.Contains(entry.Group)) continue;

            var item = LoadoutItem.Load(loadout.Db, entry.Group);
            if (!enabled && !item.IsDisabled) tx.Add(item.Id, LoadoutItem.Disabled, Null.Instance);
            else if (enabled && item.IsDisabled) tx.Retract(item.Id, LoadoutItem.Disabled, Null.Instance);
        }

        if (source.SeedVarieties) SeedVarieties(loadout, result.Resolved, tx);

        logger.LogTrace("Ordered {Count} groups of '{Source}'; {Overridden} pairs were turned round by stored rules", order.WinnerToLoser.Count, source.Name, result.Overridden.Count);

        return RuleDrops(loadout, order, result.Overridden);
    }

    /// <summary>
    /// The order as listed when every group is new. Otherwise the groups an earlier run installed keep the order they have
    /// now, which may be the user's, and each new group goes in at its listed slot among them.
    /// </summary>
    private static List<LoadoutItemGroupId> RequestedOrder(Loadout.ReadOnly loadout, ModOrder order, ModOrderSource source)
    {
        var listed = order.WinnerToLoser.Select(static entry => entry.Group).ToList();
        if (source.NewGroups is not { } added) return listed;

        var listedSet = listed.ToHashSet();
        var current = ContainerOrder.ChildrenWinnerFirst(loadout.Db, source.Container)
            .Select(static child => child.LoadoutItemGroupId)
            .Where(listedSet.Contains)
            .ToList();
        return ListedOrder.InsertNew(listed, current, added.Contains);
    }

    private const int MaxNamedMods = 5;

    /// <summary>
    /// One drop per stored rule, however many requested pairs it turned round: a rule on a collection stands for a rule
    /// on each of its mods, so one rule can reverse many pairs. The drop names every mod of those pairs in the requested
    /// order, and sends the user to File Conflicts, where stored rules are edited on every game.
    /// </summary>
    private static List<ModOrderDrop> RuleDrops(Loadout.ReadOnly loadout, ModOrder order, IReadOnlyList<OverriddenOrder> overridden)
    {
        var requested = order.WinnerToLoser.Select(static (entry, index) => (entry.Group, index)).ToDictionary(static p => p.Group, static p => p.index);
        return overridden
            .GroupBy(static pair => pair.Rule.LoadoutGroupRuleId)
            .Select(pairs =>
            {
                var mods = pairs
                    .SelectMany(static pair => new[] { pair.RequestedWinner, pair.RequestedLoser })
                    .Distinct()
                    .OrderBy(group => requested[group])
                    .Select(group => Name(loadout, group))
                    .ToList();
                var rule = GroupRuleText.WinsOver(pairs.First().Rule);
                var reason = pairs.All(static pair => pair.Displaced)
                    ? $"they were moved to make room for the stored rule '{rule}'"
                    : $"the stored rule '{rule}' puts them the other way round";
                return new ModOrderDrop($"the listed order of {NameList(mods)}", reason, FileConflictsPage);
            })
            .ToList();
    }

    /// <summary>"a", "a and b", "a, b and c"; past <see cref="MaxNamedMods"/> names the rest is counted.</summary>
    private static string NameList(IReadOnlyList<string> names)
    {
        if (names.Count > MaxNamedMods) return $"{string.Join(", ", names.Take(MaxNamedMods))} and {names.Count - MaxNamedMods} more";
        return names.Count == 1 ? names[0] : $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}";
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
