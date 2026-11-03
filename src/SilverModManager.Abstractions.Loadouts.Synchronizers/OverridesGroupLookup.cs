using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers;

/// <summary>
/// Finds a loadout's Overrides group and tells whether a loadout item sits directly in a group. The sync tree and the
/// diagnostics that read it both need this, so it lives next to <see cref="SyncNode"/>.
/// </summary>
public static class OverridesGroupLookup
{
    /// <summary>
    /// The id of the loadout's Overrides group, or the default (zero) id when it has none.
    /// </summary>
    public static EntityId Find(Loadout.ReadOnly loadout)
        => LoadoutOverridesGroup.FindByOverridesFor(loadout.Db, loadout.Id).TryGetFirst(out var found)
            ? found.Id
            : default;

    /// <summary>
    /// Whether the item's parent is <paramref name="groupId"/>. A default (zero) group id matches nothing.
    /// </summary>
    public static bool IsInGroup(IDb db, EntityId itemId, EntityId groupId)
    {
        if (groupId.Value == 0) return false;
        var item = LoadoutItem.Load(db, itemId);
        return item.IsValid() && item.HasParent() && item.ParentId.Value == groupId;
    }
}
