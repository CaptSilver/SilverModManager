using SilverModManager.Abstractions.Games.LoadOrder;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.FromSoft.Me3;

/// <summary>Reads the loadout priority of each package folder in the mods location.</summary>
public static class Me3PackagePriorities
{
    /// <summary>
    /// The priority of each package folder, as <see cref="ModUnitPriorities.Read"/> reads it for the first folder of
    /// every file in the mods location other than the overlay and natives folders.
    /// </summary>
    public static Dictionary<string, ulong> Read(Loadout.ReadOnly loadout) => ModUnitPriorities.Read(loadout, PackageOf);

    private static string? PackageOf(GamePath target)
    {
        if (target.LocationId != LocationId.Me3Mods) return null;

        var parts = target.Path.Path.Split('/', 2);
        if (parts.Length < 2) return null;
        if (parts[0].Equals(Me3ModsLayout.OverlayFolder, StringComparison.OrdinalIgnoreCase)) return null;
        if (parts[0].Equals(Me3ModsLayout.NativesFolder, StringComparison.OrdinalIgnoreCase)) return null;

        return parts[0];
    }
}
