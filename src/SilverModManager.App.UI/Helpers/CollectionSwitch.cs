using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.App.UI.Resources;
using SilverModManager.App.UI.Windows;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk;

namespace SilverModManager.App.UI.Helpers;

/// <summary>
/// The switch on a collection, wherever it is drawn. The group's own state decides which way it flips, because a
/// ToggleSwitch has already written its new position into whatever it is bound to by the time its command runs.
/// </summary>
public static class CollectionSwitch
{
    /// <summary>
    /// Flips the collection group. When the service refuses, the first reason is shown as a toast and false comes
    /// back, so the caller can put its switch back where the database still has it.
    /// </summary>
    public static async Task<bool> Flip(IConnection connection, ILoadoutManager loadoutManager, IWindowNotificationService notifications, CollectionGroupId group)
    {
        var item = LoadoutItem.Load(connection.Db, group.Value);
        var result = await loadoutManager.SetGroupsEnabled(item.LoadoutId, [item.LoadoutItemId], enabled: item.IsDisabled);
        if (result.Skipped.Count == 0) return true;

        notifications.ShowToast(string.Format(Language.CollectionLeftMenu_ToggleRefused, result.Skipped[0].Reason), ToastNotificationVariant.Failure);
        return false;
    }
}
