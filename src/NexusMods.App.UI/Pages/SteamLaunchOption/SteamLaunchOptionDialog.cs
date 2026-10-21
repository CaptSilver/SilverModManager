using Microsoft.Extensions.DependencyInjection;
using NexusMods.App.UI.Overlays;
using NexusMods.Platform.Proton.Launch;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.App.UI.Pages.SteamLaunchOption;

/// <summary>
/// Opens "Use this loadout from Steam", for the loadout card and the Health Check fix.
/// </summary>
public static class SteamLaunchOptionDialog
{
    /// <summary>
    /// Shows the dialog for the loadout and completes when the user closes it.
    /// </summary>
    public static async Task ShowAsync(IServiceProvider serviceProvider, Loadout.ReadOnly loadout)
    {
        // A captured snapshot is stale after a rename, or once files the game's requirements depend on change.
        loadout = loadout.Rebase();
        var overlay = new SteamLaunchOptionViewModel(
            loadout,
            loadout.InstallationInstance,
            serviceProvider.GetRequiredService<IProcessTracker>(),
            serviceProvider.GetRequiredService<ISteamLaunchOptionWriter>(),
            serviceProvider.GetRequiredService<IAvaloniaInterop>()
        );
        await serviceProvider.GetRequiredService<IOverlayController>().EnqueueAndWait(overlay);
    }
}
