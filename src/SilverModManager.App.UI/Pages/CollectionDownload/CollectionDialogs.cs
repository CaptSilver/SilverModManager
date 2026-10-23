using SilverModManager.App.UI.Dialog;
using SilverModManager.App.UI.Pages.CollectionDownload.Dialogs.PremiumDownloads;
using SilverModManager.App.UI.Resources;
using SilverModManager.UI.Sdk.Dialog;
using SilverModManager.UI.Sdk.Dialog.Enums;
using SilverModManager.UI.Sdk.Icons;

namespace SilverModManager.App.UI.Pages.CollectionDownload;

public static class CollectionDialogs
{
    public static IDialog PremiumCollectionDialog()
    {
        var premiumCollectionDownloadsViewModel = new DialogPremiumCollectionDownloadsViewModel();

        return DialogFactory.CreateDialog(Language.DialogPremiumCollection_DialogTitle,
            [
                new DialogButtonDefinition(
                    Language.DialogPremiumCollection_Cancel,
                    ButtonDefinitionId.From("cancel"),
                    ButtonAction.Reject
                ),
                new DialogButtonDefinition(
                    Language.DialogPremiumCollection_UpgradeToPremium,
                    ButtonDefinitionId.From("go-premium"),
                    ButtonAction.Accept,
                    ButtonStyling.Premium,
                    IconValues.Premium
                )
            ],
            premiumCollectionDownloadsViewModel
        );
    }

}
