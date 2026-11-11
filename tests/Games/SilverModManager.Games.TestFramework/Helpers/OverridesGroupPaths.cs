using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.TestFramework;

/// <summary>Reads what a loadout's Overrides group holds, for tests that check which files a sync left as external changes.</summary>
public static class OverridesGroupPaths
{
    /// <summary>
    /// The paths, without their location, of the files in the Overrides group of <paramref name="loadout"/> as of
    /// <paramref name="db"/>. Pass the connection's current database: a loadout read earlier shows the older state.
    /// </summary>
    public static string[] OverridesPaths(this IDb db, Loadout.ReadOnly loadout) =>
        LoadoutOverridesGroup.FindByOverridesFor(db, loadout.Id)
            .SelectMany(group => group.AsLoadoutItemGroup().Children.OfTypeLoadoutItemWithTargetPath())
            .Select(item => ((GamePath)item.TargetPath).Path.ToString())
            .ToArray();
}
