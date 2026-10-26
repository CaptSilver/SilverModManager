using DynamicData.Kernel;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.DataModel;

internal partial class LoadoutManager
{
    /// <summary>
    /// Adds an empty group to the loadout in <paramref name="tx"/>. A top-level group also gets a priority row
    /// above the existing ones, the way an installed mod does; a group with a <paramref name="parent"/> is
    /// ordered by the parent and gets none.
    /// </summary>
    public static LoadoutItemGroupId AddEmptyGroup(ITransaction tx, LoadoutId loadoutId, string name, Optional<LoadoutItemGroupId> parent = default)
    {
        var group = new LoadoutItemGroup.New(tx, out var id)
        {
            IsGroup = true,
            LoadoutItem = NewGroupItem(tx, id, loadoutId, name, parent),
        };

        if (!parent.HasValue)
            tx.Add(new AddPriorityTxFunc(loadoutId, group.Id));

        return group.Id;
    }

    private static LoadoutItem.New NewGroupItem(ITransaction tx, EntityId id, LoadoutId loadoutId, string name, Optional<LoadoutItemGroupId> parent)
    {
        var item = new LoadoutItem.New(tx, id)
        {
            LoadoutId = loadoutId,
            Name = name,
        };
        // Parent is an optional attribute, so it is only written when there is one.
        if (parent.HasValue) item.ParentId = parent.Value;
        return item;
    }
}
