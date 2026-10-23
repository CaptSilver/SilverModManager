using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Extensions;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.FromSoft.Me3;

/// <summary>Reads the loadout priority of each package folder in the mods location.</summary>
public static class Me3PackagePriorities
{
    /// <summary>
    /// The priority of each package folder, taken from the first group at or above one of its files that has a
    /// priority row. Disabled files are skipped, since they are not deployed. When several mods put files in one
    /// folder, the highest of their priorities stands for it.
    /// </summary>
    public static Dictionary<string, ulong> Read(Loadout.ReadOnly loadout)
    {
        var byGroup = new Dictionary<EntityId, ulong>();
        foreach (var row in LoadoutItemGroupPriority.FindByLoadout(loadout.Db, loadout))
            byGroup[row.TargetId] = row.Priority.Value;

        var priorities = new Dictionary<string, ulong>(StringComparer.Ordinal);
        foreach (var item in LoadoutItem.FindByLoadout(loadout.Db, loadout).OfTypeLoadoutItemWithTargetPath())
        {
            var loadoutItem = item.AsLoadoutItem();
            if (loadoutItem.GetThisAndParents().Any(p => p.IsDisabled)) continue;

            var target = (GamePath)item.TargetPath;
            if (target.LocationId != LocationId.Me3Mods) continue;

            var parts = target.Path.Path.Split('/', 2);
            if (parts.Length < 2) continue;
            if (parts[0].Equals(Me3ModsLayout.OverlayFolder, StringComparison.OrdinalIgnoreCase)) continue;
            if (parts[0].Equals(Me3ModsLayout.NativesFolder, StringComparison.OrdinalIgnoreCase)) continue;

            foreach (var ancestor in loadoutItem.GetThisAndParents())
            {
                if (!byGroup.TryGetValue(ancestor.Id, out var priority)) continue;

                if (!priorities.TryGetValue(parts[0], out var known) || priority > known) priorities[parts[0]] = priority;
                break;
            }
        }

        return priorities;
    }
}
