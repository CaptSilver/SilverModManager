using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.App.UI.Resources;
using SilverModManager.App.UI.Windows;
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
    /// Synchronizes the game to <paramref name="loadoutId"/> and toasts the result. A failure is explained to the
    /// player in a dialog (a refusal in its own words, with a button to the Health Check when the synchronizer
    /// refused up front and nothing was written; anything else as a crash) and returns false.
    /// </summary>
    public static async Task<bool> Apply(IServiceProvider serviceProvider, LoadoutId loadoutId)
    {
        var syncService = serviceProvider.GetRequiredService<ISynchronizerService>();
        var notificationService = serviceProvider.GetRequiredService<IWindowNotificationService>();

        try
        {
            await Task.Run(() => syncService.Synchronize(loadoutId));
            notificationService.ShowToast(Language.ToastNotification_Applied__0__successfully, ToastNotificationVariant.Success);
            return true;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Left unhandled this only reaches the log, so the Apply button would just stop spinning.
            await serviceProvider.GetRequiredService<IErrorPresenter>().Report(e, "applying the loadout");
            return false;
        }
    }
}
