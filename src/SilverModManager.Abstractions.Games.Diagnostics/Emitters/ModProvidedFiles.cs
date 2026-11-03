using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Diagnostics.Emitters;

/// <summary>
/// Tells the sync tree's nodes that a mod provides from the ones the loadout holds for another reason. A file
/// adopted into the Overrides group is a row of the loadout, but it is a leftover from the game folder and not
/// something the user installed, so it does not count as provided.
/// </summary>
/// <remarks>
/// <see cref="SyncNode.PlayerDeleted"/> is only set for a node whose file is missing from disk, so the group
/// is looked up here through the item the node's loadout side points at.
/// </remarks>
public sealed class ModProvidedFiles
{
    private readonly IDb _db;
    private readonly EntityId _overridesGroup;

    private ModProvidedFiles(IDb db, EntityId overridesGroup)
    {
        _db = db;
        _overridesGroup = overridesGroup;
    }

    /// <summary>
    /// Reads the loadout's Overrides group, if it has one.
    /// </summary>
    public static ModProvidedFiles For(Loadout.ReadOnly loadout)
    {
        return new ModProvidedFiles(loadout.Db, OverridesGroupLookup.Find(loadout));
    }

    /// <summary>
    /// Whether the node's loadout side is a file of a mod: a loadout row that is not in the Overrides group. The
    /// sync tree holds the rows of enabled mods only, so a disabled mod's file is not provided.
    /// </summary>
    public bool IsProvidedByMod(in SyncNode node)
    {
        if (node.SourceItemType != LoadoutSourceItemType.Loadout || !node.HaveLoadout || !node.Loadout.HaveEntityId) return false;
        return !IsInOverrides(node.Loadout.EntityId);
    }

    /// <summary>
    /// Whether the node's loadout side is a row of the Overrides group.
    /// </summary>
    public bool IsInOverrides(EntityId item)
    {
        return OverridesGroupLookup.IsInGroup(_db, item, _overridesGroup);
    }
}
