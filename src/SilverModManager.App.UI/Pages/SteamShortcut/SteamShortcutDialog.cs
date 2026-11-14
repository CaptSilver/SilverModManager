using Microsoft.Extensions.DependencyInjection;
using SilverModManager.App.UI.Overlays;
using SilverModManager.App.UI.Windows;
using SilverModManager.Platform.Proton.Steam;
using SilverModManager.Platform.Proton.Steam.Shortcuts;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;

namespace SilverModManager.App.UI.Pages.SteamShortcut;

/// <summary>
/// Opens "Show in Steam", beside the dialog that writes a loadout's launch options.
/// </summary>
public static class SteamShortcutDialog
{
    /// <summary>
    /// Shows the dialog for the current state and completes when the user closes it.
    /// </summary>
    public static async Task ShowAsync(IServiceProvider serviceProvider)
    {
        var service = serviceProvider.GetRequiredService<ISteamShortcutService>();
        var processTracker = serviceProvider.GetRequiredService<IProcessTracker>();
        var steamUris = serviceProvider.GetRequiredService<ISteamUriLauncher>();
        var closeAndWrite = SteamCloseAndWrite.Create(
            processTracker.IsSteamRunning,
            () => processTracker.FindByName("steamwebhelper").Count > 0,
            () => steamUris.Open(new Uri("steam://exit")),
            () => steamUris.Open(new Uri("steam://open/main")));

        var overlay = new SteamShortcutViewModel(
            service,
            processTracker,
            closeAndWrite,
            serviceProvider.GetRequiredService<IErrorPresenter>(),
            await Task.Run(() => service.Status()),
            serviceProvider.GetRequiredService<IGameModeDetector>().IsGameMode);
        await serviceProvider.GetRequiredService<IOverlayController>().EnqueueAndWait(overlay);
    }
}
