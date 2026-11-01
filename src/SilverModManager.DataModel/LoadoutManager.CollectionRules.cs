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
    /// a rule on those. <paramref name="moves"/> are re-parentings the caller's transaction is about to write (group to
    /// its new collection); a tx function can't see them, so they are applied here over the stored parents.
    /// </summary>
    internal static Dictionary<LoadoutItemGroupId, LoadoutItemGroupId[]> CollectionMembers(
        IDb db, LoadoutId loadoutId, IReadOnlyDictionary<LoadoutItemGroupId, LoadoutItemGroupId>? moves = null)
    {
        var members = new Dictionary<LoadoutItemGroupId, List<LoadoutItemGroupId>>();
        foreach (var row in LoadoutItemGroupPriority.FindByLoadout(db, loadoutId).OrderBy(static row => row.Priority))
        {
            if (!row.Target.IsValid()) continue;

            var item = row.Target.AsLoadoutItem();
            LoadoutItemGroupId parent;
            if (moves is not null && moves.TryGetValue(row.TargetId, out var moved)) parent = moved;
            else if (item.HasParent()) parent = item.ParentId.Value;
            else continue;

            if (!CollectionGroup.Load(db, parent).IsValid()) continue;

            if (!members.TryGetValue(parent, out var list)) members[parent] = list = [];
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
