using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.Networking.NexusWebApi;
using SilverModManager.App.UI.Windows;
using SilverModManager.Sdk;
using SilverModManager.UI.Sdk.Dialog;
using SilverModManager.App.UI.Dialog.Enums;
using SilverModManager.UI.Sdk.Dialog.Enums;

namespace SilverModManager.App.UI.Pages.CollectionDownload;

/// <summary>
/// The question that comes before downloading a Nexus collection's mods: is the user logged in, and do they have
/// premium. Shared by the revision page's download buttons and the collection update.
/// </summary>
public static class CollectionDownloadGate
{
    /// <summary>
    /// Logs the user in if needed. Without premium it shows the premium dialog (and opens the upgrade page if they
    /// choose it) and says no.
    /// </summary>
    public static async Task<bool> EnsureCanDownloadCollections(ILoginManager loginManager, IWindowManager windowManager, IOSInterop osInterop, CancellationToken cancellationToken)
    {
        if (!await loginManager.EnsureLoggedIn("Download Collection", cancellationToken)) return false;
        if (loginManager.IsPremium) return true;

        var dialogResult = await windowManager.ShowDialog(CollectionDialogs.PremiumCollectionDialog(), DialogWindowType.Modal);
        if (dialogResult.ButtonId == ButtonDefinitionId.From("go-premium")) osInterop.OpenUri(NexusModsUrlBuilder.UpgradeToPremiumUri);
        return false;
    }
}
