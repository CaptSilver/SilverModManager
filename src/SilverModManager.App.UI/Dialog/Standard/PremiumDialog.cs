using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.App.UI.Dialog.Enums;
using SilverModManager.App.UI.Resources;
using SilverModManager.App.UI.Windows;
using SilverModManager.Sdk;
using SilverModManager.UI.Sdk.Dialog;
using SilverModManager.UI.Sdk.Dialog.Enums;
using SilverModManager.UI.Sdk.Icons;

namespace SilverModManager.App.UI.Dialog.Standard;

/// <summary>
/// Helper class for showing premium upgrade dialogs
/// </summary>
public static class PremiumDialog
{
    /// <summary>
    /// Shows the premium upgrade dialog for mod updates
    /// </summary>
    /// <param name="windowManager">Window manager to show the dialog</param>
    /// <param name="osInterop">OS interop service for opening URLs</param>
    /// <returns>Task representing the dialog result</returns>
    public static async Task<ButtonDefinitionId> ShowUpdatePremiumDialog(IWindowManager windowManager, IOSInterop osInterop)
    {
        var dialog = DialogFactory.CreateStandardDialog(
            title: Language.PremiumDialog_UpdateTitle,
            new StandardDialogParameters()
            {
                Heading = Language.PremiumDialog_UpdateHeading,
                Text = Language.PremiumDialog_UpdateDescription,
                Icon = IconValues.PictogramPremium,
            },
            buttonDefinitions:
            [
                new DialogButtonDefinition(
                    Language.PremiumDialog_UpdateManuallyButton,
                    ButtonDefinitionId.From("update-manually"),
                    ButtonAction.Reject
                ),
                new DialogButtonDefinition(
                    Language.PremiumDialog_GoPremiumButton,
                    ButtonDefinitionId.From("go-premium"),
                    ButtonAction.Accept,
                    ButtonStyling.Primary
                )
            ],
            windowSize: DialogWindowSize.Medium
        );

        var result = await windowManager.ShowDialog(dialog, DialogWindowType.Modal);
        
        if (result.ButtonId == ButtonDefinitionId.From("go-premium"))
        {
            osInterop.OpenUri(NexusModsUrlBuilder.LearnAboutPremiumUri);
        }

        return result.ButtonId;
    }
} 
