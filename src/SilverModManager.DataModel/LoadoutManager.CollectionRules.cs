using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.DataModel.Sorting;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.DataModel;

internal partial class LoadoutManager
{
    /// <summary>
    /// The mods of each collection that has any in the loadout's priority order, lowest priority first. A collection
    /// is not ordered on its own: it is the mods that carry its group as their parent, so a rule on a collection is
    /// a rule on those.
    /// </summary>
    internal static Dictionary<LoadoutItemGroupId, LoadoutItemGroupId[]> CollectionMembers(IDb db, LoadoutId loadoutId)
    {
        var members = new Dictionary<LoadoutItemGroupId, List<LoadoutItemGroupId>>();
        foreach (var row in LoadoutItemGroupPriority.FindByLoadout(db, loadoutId).OrderBy(static row => row.Priority))
        {
            if (!row.Target.IsValid()) continue;

            var item = row.Target.AsLoadoutItem();
            if (!item.HasParent() || !CollectionGroup.Load(db, item.ParentId.Value).IsValid()) continue;

            if (!members.TryGetValue(item.ParentId, out var list)) members[item.ParentId] = list = [];
            list.Add(row.TargetId);
        }

        return members.ToDictionary(static pair => pair.Key, static pair => pair.Value.ToArray());
    }

    /// <summary>
    /// <paramref name="rules"/> with every rule that names a collection replaced by the rules between mods it stands for.
    /// </summary>
    private static List<(GroupRule Rule, int Origin)> ExpandCollectionRules(IDb db, LoadoutId loadoutId, IReadOnlyList<GroupRule> rules)
        => RuleResolver.ExpandCollections(rules, CollectionMembers(db, loadoutId));
}
