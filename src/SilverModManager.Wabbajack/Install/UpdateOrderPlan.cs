using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SilverModManager.Abstractions.Games.LoadOrder;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Wabbajack.Mapping;

namespace SilverModManager.Wabbajack.Install;

/// <summary>
/// What an update does to the mod order and switches of the kept groups.
/// </summary>
/// <param name="NewGroups">
/// The groups the order sink places at their listed slots: the ones the new list adds, and the ones it moved that the user
/// left where they were. Every other group keeps the place it has.
/// </param>
/// <param name="FlagWrites">Kept groups outside <paramref name="NewGroups"/> whose switch the list changed and the user had not.</param>
/// <param name="Conflicts">Kept groups whose place the user changed and the list moved elsewhere; the user's place stands.</param>
/// <param name="Entries">The new order with each kept group's switch merged, so the sink never undoes a switch the user made.</param>
internal sealed record UpdateOrderPlan(
    IReadOnlySet<LoadoutItemGroupId> NewGroups,
    IReadOnlyList<(LoadoutItemGroupId Group, bool Enabled)> FlagWrites,
    IReadOnlyList<string> Conflicts,
    IReadOnlyList<ModOrderEntry> Entries);

/// <summary>
/// The three-way rule for an updated list's mod order and switches, with the previous version's profile as the base, the
/// new version's profile as one side and the user's loadout as the other (<see cref="OrderDelta"/>).
/// </summary>
internal static class UpdateOrderPlanner
{
    /// <summary>Shown when the library no longer holds the installed version, so there is nothing to tell which files the user changed or kept.</summary>
    public const string InstalledVersionGoneWarning = "the installed version's files are gone from the library; the list's order and flags are applied in full";

    /// <summary>
    /// A group the old profile placed is kept: it moves only when the list moved it and the user did not, and its switch
    /// follows the list only when the user left it. A group the old profile did not place is new. A kept group that has no
    /// line in the old modlist.txt was installed on, which is its switch before the list says anything about it.
    /// </summary>
    /// <param name="old">The previous profile's order over the groups that existed before the update.</param>
    /// <param name="new">The new profile's order over every group.</param>
    /// <param name="user">The loadout's order and switches now; groups outside <paramref name="old"/> are ignored.</param>
    public static UpdateOrderPlan Plan(
        IReadOnlyList<ModOrderEntry> old, IReadOnlyList<ModOrderEntry> @new, IReadOnlyList<ModOrderEntry> user, Func<LoadoutItemGroupId, string> nameOf)
    {
        var kept = old.Select(e => e.Group).Intersect(@new.Select(e => e.Group)).ToHashSet();
        var oldKept = old.Where(e => kept.Contains(e.Group)).ToArray();
        var newKept = @new.Where(e => kept.Contains(e.Group)).ToArray();
        var userKept = user.Where(e => kept.Contains(e.Group)).ToArray();

        var oldPlace = Predecessors(oldKept);
        var newPlace = Predecessors(newKept);
        var userPlace = Predecessors(userKept);
        var userFlag = userKept.ToDictionary(e => e.Group, e => e.Enabled);
        var oldFlag = oldKept.ToDictionary(e => e.Group, e => e.Enabled);

        var newGroups = @new.Select(e => e.Group).Where(g => !kept.Contains(g)).ToHashSet();
        var conflicts = new List<string>();
        var movedByList = OrderDelta.Moved(oldKept.Select(e => e.Group).ToArray(), newKept.Select(e => e.Group).ToArray());
        foreach (var group in newKept.Select(e => e.Group).Where(movedByList.Contains))
        {
            var (place, conflict) = OrderDelta.ThreeWay(oldPlace[group], newPlace[group], userPlace.GetValueOrDefault(group));
            if (conflict) conflicts.Add(nameOf(group));
            else if (!Equals(place, userPlace.GetValueOrDefault(group))) newGroups.Add(group);
        }

        var entries = new List<ModOrderEntry>(@new.Count);
        var flagWrites = new List<(LoadoutItemGroupId, bool)>();
        foreach (var entry in @new)
        {
            if (!kept.Contains(entry.Group) || entry.Enabled is not { } listed)
            {
                entries.Add(entry);
                continue;
            }

            var mine = userFlag.GetValueOrDefault(entry.Group) ?? true;
            // Two bools cannot conflict: a list value that differs from both the base and the user's is the other value of the same two.
            var (merged, _) = OrderDelta.ThreeWay(oldFlag.GetValueOrDefault(entry.Group) ?? true, listed, mine);
            if (merged != mine && !newGroups.Contains(entry.Group)) flagWrites.Add((entry.Group, merged));
            entries.Add(entry with { Enabled = merged });
        }
        return new UpdateOrderPlan(newGroups, flagWrites, conflicts, entries);
    }


    /// <summary>
    /// The plan for the groups of <paramref name="container"/> now: the previous profile is read over the groups that
    /// <paramref name="existed"/> before the update, the user's side is the container's order and switches as stored.
    /// </summary>
    /// <param name="children">The groups to order: every group of the container the plan is about.</param>
    /// <param name="existed">The groups the previous version had.</param>
    /// <param name="oldEntries">The previous modlist.txt, spelled with the names <paramref name="children"/> carry.</param>
    /// <param name="newOrder">The new profile read over <paramref name="children"/>.</param>
    public static UpdateOrderPlan ForContainer(
        IDb db, LoadoutItemGroupId container, IReadOnlyList<LoadoutItemGroup.ReadOnly> children, IReadOnlySet<LoadoutItemGroupId> existed,
        IReadOnlyList<ModlistTxtEntry> oldEntries, ModOrder newOrder, IReadOnlySet<string> gameRootGroupNames, string listName)
    {
        var old = Mo2ProfileOrderReader.Read(
            children.Where(c => existed.Contains(c.LoadoutItemGroupId)).ToArray(), oldEntries, gameRootGroupNames, listName, NullLogger.Instance).WinnerToLoser;
        var user = ContainerOrder.ChildrenWinnerFirst(db, container)
            .Where(c => existed.Contains(c.LoadoutItemGroupId))
            .Select(c => new ModOrderEntry(c.LoadoutItemGroupId, !LoadoutItem.Load(db, c.Id).IsDisabled))
            .ToArray();
        return Plan(old, newOrder.WinnerToLoser, user, group => LoadoutItem.Load(db, group).Name);
    }

    /// <summary>The lines of a modlist.txt with each folder named in <paramref name="renamed"/> spelled under its other name.</summary>
    public static IReadOnlyList<ModlistTxtEntry> Rename(IReadOnlyList<ModlistTxtEntry> entries, IEnumerable<(string From, string To)> renamed)
    {
        var map = renamed.ToDictionary(r => r.From, r => r.To, StringComparer.OrdinalIgnoreCase);
        return entries.Select(e => map.TryGetValue(e.Name, out var to) ? e with { Name = to } : e).ToArray();
    }

    private static Dictionary<LoadoutItemGroupId, LoadoutItemGroupId?> Predecessors(IReadOnlyList<ModOrderEntry> order)
    {
        var result = new Dictionary<LoadoutItemGroupId, LoadoutItemGroupId?>();
        for (var i = 0; i < order.Count; i++) result[order[i].Group] = i == 0 ? null : order[i - 1].Group;
        return result;
    }
}
