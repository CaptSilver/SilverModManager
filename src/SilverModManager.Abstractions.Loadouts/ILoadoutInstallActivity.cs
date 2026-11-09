using SilverModManager.Abstractions.Loadouts.Exceptions;
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

    /// <summary>
    /// Why applying <paramref name="loadout"/> is refused when <see cref="IsInstallingInto"/> is true, in words for the
    /// player, or null when the plain "an install is still running" says it. An update that stopped part way is one:
    /// nothing is running, but the loadout holds half a list.
    /// </summary>
    string? InstallBlockReason(LoadoutId loadout) => null;

    /// <summary>The refusal Apply throws while <see cref="IsInstallingInto"/> is true: the plain one, or one titled and worded for <see cref="InstallBlockReason"/>.</summary>
    LoadoutInstallRunningException BlockedApply(LoadoutId loadout)
        => InstallBlockReason(loadout) is { } reason ? new LoadoutInstallRunningException(reason, "The update is not finished") : new LoadoutInstallRunningException();

    /// <summary>The current answer of <see cref="IsInstallingInto"/>, then every change of it.</summary>
    IObservable<bool> ObserveInstallingInto(LoadoutId loadout);

    /// <summary>
    /// Whether something is running and writing into <paramref name="loadout"/> right now, then every change. Narrower
    /// than <see cref="ObserveInstallingInto"/>: an update that stopped part way keeps the loadout from being applied,
    /// but nothing is writing, so what the loadout holds is not about to change under a comparison.
    /// </summary>
    IObservable<bool> ObserveWritingInto(LoadoutId loadout);
}
