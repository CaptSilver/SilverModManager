using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Extensions;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Games.LoadOrder;

/// <summary>
/// Reads where the loadout ranks each unit a game loads separately (a package folder, a mod folder), for games that
/// are told that order in a file the app generates. The ranking is the loadout's own priority rows, so the Load Order
/// page, File Conflicts and the generated file never disagree.
/// </summary>
public static class ModUnitPriorities
{
    /// <summary>
    /// The priority of each unit, taken from the first group at or above one of its files that has a priority row.
    /// <paramref name="unitOf"/> names the unit a file belongs to, or null for a file that is no unit's. Disabled files
    /// are skipped, since they are not deployed. When several mods put files in one unit, the highest of their
    /// priorities stands for it. A unit whose files sit under no ranked group is not in the result.
    /// </summary>
    public static Dictionary<string, ulong> Read(Loadout.ReadOnly loadout, Func<GamePath, string?> unitOf)
    {
        var byGroup = new Dictionary<EntityId, ulong>();
        foreach (var row in LoadoutItemGroupPriority.FindByLoadout(loadout.Db, loadout))
            byGroup[row.TargetId] = row.Priority.Value;

        var priorities = new Dictionary<string, ulong>(StringComparer.Ordinal);
        foreach (var item in LoadoutItem.FindByLoadout(loadout.Db, loadout).OfTypeLoadoutItemWithTargetPath())
        {
            var loadoutItem = item.AsLoadoutItem();
            if (loadoutItem.GetThisAndParents().Any(p => p.IsDisabled)) continue;

            if (unitOf(item.TargetPath) is not { } unit) continue;

            foreach (var ancestor in loadoutItem.GetThisAndParents())
            {
                if (!byGroup.TryGetValue(ancestor.Id, out var priority)) continue;

                if (!priorities.TryGetValue(unit, out var known) || priority > known) priorities[unit] = priority;
                break;
            }
        }

        return priorities;
    }
}
