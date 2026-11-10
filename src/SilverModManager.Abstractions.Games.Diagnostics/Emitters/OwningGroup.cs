using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Diagnostics.Emitters;

/// <summary>
/// Finds the group a sync node's loadout row sits in. A diagnostic that points at a mod needs the group: the
/// node's own entity is a file row, which a group reference renders as an invalid entity.
/// </summary>
public static class OwningGroup
{
    /// <summary>
    /// The group holding the node's loadout row, the Overrides group included. Null for a node without a loadout
    /// row, and for a row with no parent.
    /// </summary>
    public static EntityId? Of(IDb db, in SyncNode node)
    {
        if (!node.HaveLoadout || !node.Loadout.HaveEntityId) return null;
        var item = LoadoutItem.Load(db, node.Loadout.EntityId);
        return item.IsValid() && item.HasParent() ? item.ParentId : null;
    }
}
