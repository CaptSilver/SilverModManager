using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Extensions;
using NexusMods.Games.FromSoft.Me3;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Games.FromSoft.EldenRing;

/// <summary>Whether a loadout changes what the game loads, which is what decides if it runs through me3.</summary>
public static class ModdedLoadout
{
    /// <summary>True when any enabled file of the loadout sits in the mods folder: a package, a native or an overlay file.</summary>
    public static bool IsModded(Loadout.ReadOnly loadout)
        => EnabledTargets(loadout).Any(path => path.LocationId == LocationId.Me3Mods);

    /// <summary>True when the loadout has the me3 binary, enabled, in the me3 host location.</summary>
    public static bool HasHost(Loadout.ReadOnly loadout)
        => EnabledTargets(loadout).Contains(new GamePath(LocationId.Me3Host, Me3HostFiles.Binary));

    private static IEnumerable<GamePath> EnabledTargets(Loadout.ReadOnly loadout)
        => LoadoutItem.FindByLoadout(loadout.Db, loadout)
            .OfTypeLoadoutItemWithTargetPath()
            .Where(item => item.AsLoadoutItem().IsEnabled())
            .Select(item => (GamePath)item.TargetPath);
}
