using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Loadouts;

/// <summary>
/// Knows which loadouts something is installing into right now (a modlist install writes thousands of files over
/// many minutes). Applying such a loadout would put a half-installed list into the game folder, so the Apply
/// button stays off and the synchronizer refuses while this is true.
/// </summary>
public interface ILoadoutInstallActivity
{
    /// <summary>True from the moment an install into <paramref name="loadout"/> begins until it completes, fails or is cancelled.</summary>
    bool IsInstallingInto(LoadoutId loadout);

    /// <summary>The current answer of <see cref="IsInstallingInto"/>, then every change of it.</summary>
    IObservable<bool> ObserveInstallingInto(LoadoutId loadout);
}
