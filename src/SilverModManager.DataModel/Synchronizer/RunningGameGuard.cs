using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts.Exceptions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.DataModel.Synchronizer;

/// <summary>
/// Refuses the three writers (apply, delete a loadout, unmanage a game) while a launch from this app is running.
/// </summary>
internal static class RunningGameGuard
{
    public static void ThrowIfRunning(IServiceProvider serviceProvider, Loadout.ReadOnly loadout)
        => ThrowIfRunning(serviceProvider, loadout.InstallationInstance, loadout.Name);

    public static void ThrowIfRunning(IServiceProvider serviceProvider, GameInstallation installation, string? loadoutName = null)
    {
        // Resolved per call: the registry lives in the platform project, and hosts that never launch a game
        // (and never write) shouldn't have to register one to build the data model.
        var launches = serviceProvider.GetRequiredService<IGameLaunchSession>();
        if (!launches.IsRunning(installation)) return;

        // The game has exited by then and a retry in a moment works, so don't tell the person to quit it.
        if (launches.IsFinishing(installation))
            throw new ExecutableInUseException($"{installation.Game.DisplayName} is being finished after its last session in SilverModManager. Wait a moment, then try again.");

        var which = loadoutName is null ? "" : $" (loadout {loadoutName})";
        throw new ExecutableInUseException(
            $"{installation.Game.DisplayName} is running from SilverModManager{which}. Quit the game before applying, deleting or removing."
        );
    }
}
