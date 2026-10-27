using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Games.LoadOrder;

/// <summary>How the groups of a collection or list sit in the loadout's priority order.</summary>
public static class ContainerOrder
{
    /// <summary>
    /// The direct child groups of <paramref name="container"/>, highest priority first. A group with more than one
    /// priority row sits at its highest; a group with none sorts last, in a stable order.
    /// </summary>
    public static LoadoutItemGroup.ReadOnly[] ChildrenWinnerFirst(IDb db, LoadoutItemGroupId container)
    {
        var parent = LoadoutItemGroup.Load(db, container);

        // Two rows for one target do happen (an interrupted move, a merge of loadouts), and a lookup must not throw on them.
        var priorities = LoadoutItemGroupPriority
            .FindByLoadout(db, parent.AsLoadoutItem().LoadoutId)
            .GroupBy(static row => row.TargetId)
            .ToDictionary(static rows => rows.Key, static rows => rows.Max(static row => row.Priority.Value));

        return parent.Children
            .OfTypeLoadoutItemGroup()
            .OrderByDescending(child => priorities.TryGetValue(child.LoadoutItemGroupId, out var priority) ? (long)priority : -1L)
            .ThenBy(static child => child.LoadoutItemGroupId.Value)
            .ToArray();
    }
}
