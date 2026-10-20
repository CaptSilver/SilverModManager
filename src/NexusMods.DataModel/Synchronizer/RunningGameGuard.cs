using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts.Exceptions;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.DataModel.Synchronizer;

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
        if (!serviceProvider.GetRequiredService<IGameLaunchSession>().IsRunning(installation)) return;

        var which = loadoutName is null ? "" : $" (loadout {loadoutName})";
        throw new ExecutableInUseException(
            $"{installation.Game.DisplayName} is running from SilverModManager{which}. Quit the game before applying, deleting or removing."
        );
    }
}
