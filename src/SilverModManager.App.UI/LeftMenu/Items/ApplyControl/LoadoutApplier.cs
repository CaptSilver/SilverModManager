using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.App.UI.Overlays.Generic.MessageBox.Ok;
using SilverModManager.App.UI.Resources;
using SilverModManager.App.UI.Windows;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk;

namespace SilverModManager.App.UI.LeftMenu.Items;

/// <summary>
/// Applies a loadout to its game the way the left menu's Apply button does, so every place that offers to apply
/// one syncs the previous loadout back, resets the game and deploys the new one in the same way.
/// </summary>
public static class LoadoutApplier
{
    /// <summary>
    /// Synchronizes the game to <paramref name="loadoutId"/> and toasts the result. When a game launched from the
    /// app is still running, explains that in a dialog instead and returns false; any other failure propagates.
    /// </summary>
    public static async Task<bool> Apply(IServiceProvider serviceProvider, LoadoutId loadoutId)
    {
        var syncService = serviceProvider.GetRequiredService<ISynchronizerService>();
        var notificationService = serviceProvider.GetRequiredService<IWindowNotificationService>();
        var loadout = Sdk.Loadouts.Loadout.Load(serviceProvider.GetRequiredService<IConnection>().Db, loadoutId);

        return await MessageBoxOkViewModel.RunOrExplainRunningGame(serviceProvider, loadout.Installation.Name, async () =>
        {
            await Task.Run(() => syncService.Synchronize(loadoutId));
            notificationService.ShowToast(Language.ToastNotification_Applied__0__successfully, ToastNotificationVariant.Success);
        });
    }
}
