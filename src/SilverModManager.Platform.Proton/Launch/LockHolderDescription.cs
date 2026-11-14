using SilverModManager.Abstractions.Games;

namespace SilverModManager.Platform.Proton.Launch;

/// <summary>
/// Says who holds an installation's launch lock, for a launch that was refused and for a writer that must wait.
/// </summary>
internal static class LockHolderDescription
{
    /// <summary>The player-facing sentence for the holder named in the lock's state file.</summary>
    public static string Of(string game, InstallationLock installationLock) => Of(game, ReadState(installationLock));

    /// <summary>The state file's holder, or null when it is missing or unreadable.</summary>
    public static InstallationLockState? ReadState(InstallationLock installationLock)
    {
        try
        {
            return installationLock.ReadState();
        }
        catch (InvalidDataException)
        {
            // A half-written or foreign state file only costs the details.
            return null;
        }
    }

    /// <summary>The player-facing sentence for <paramref name="state"/>, as <see cref="ReadState"/> returned it.</summary>
    public static string Of(string game, InstallationLockState? state)
    {
        if (state is { Phase: LaunchPhase.Finishing })
            return $"{game} is being finished after its last session by SilverModManager process {state.Pid} (loadout '{state.Loadout}').";

        return state is null
            ? $"{game} is already being launched by another SilverModManager process."
            : $"{game} is already being launched by SilverModManager process {state.Pid} (loadout '{state.Loadout}', {state.Phase.ToString().ToLowerInvariant()}).";
    }
}
