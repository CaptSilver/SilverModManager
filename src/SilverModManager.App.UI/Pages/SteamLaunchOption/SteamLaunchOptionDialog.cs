using Microsoft.Extensions.DependencyInjection;
using SilverModManager.App.UI.Overlays;
using SilverModManager.Platform.Proton.Launch;
using SilverModManager.Platform.Proton.Steam;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.App.UI.Pages.SteamLaunchOption;

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
        var processTracker = serviceProvider.GetRequiredService<IProcessTracker>();
        var osInterop = serviceProvider.GetRequiredService<IOSInterop>();
        // The same steam:// URLs the app already hands to the OS to start a game through Steam.
        var closeAndWrite = SteamCloseAndWrite.Create(
            processTracker.IsSteamRunning,
            () => processTracker.FindByName("steamwebhelper").Count > 0,
            () => osInterop.OpenUri(new Uri("steam://exit")),
            () => osInterop.OpenUri(new Uri("steam://open/main"))
        );
        var overlay = new SteamLaunchOptionViewModel(
            loadout,
            loadout.InstallationInstance,
            processTracker,
            serviceProvider.GetRequiredService<ISteamLaunchOptionWriter>(),
            serviceProvider.GetRequiredService<IAvaloniaInterop>(),
            closeAndWrite
        );
        await serviceProvider.GetRequiredService<IOverlayController>().EnqueueAndWait(overlay);
    }
}
