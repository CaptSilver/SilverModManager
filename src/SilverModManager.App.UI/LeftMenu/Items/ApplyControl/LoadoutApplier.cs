using DynamicData.Kernel;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.App.UI.Controls.MarkdownRenderer;
using SilverModManager.App.UI.Controls.Navigation;
using SilverModManager.App.UI.Dialog;
using SilverModManager.App.UI.Dialog.Enums;
using SilverModManager.App.UI.Overlays.Generic.MessageBox.Ok;
using SilverModManager.App.UI.Pages.Diagnostics;
using SilverModManager.App.UI.Resources;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk;
using SilverModManager.UI.Sdk.Dialog;
using SilverModManager.UI.Sdk.Dialog.Enums;

namespace SilverModManager.App.UI.LeftMenu.Items;

/// <summary>
/// Applies a loadout to its game the way the left menu's Apply button does, so every place that offers to apply
/// one syncs the previous loadout back, resets the game and deploys the new one in the same way.
/// </summary>
public static class LoadoutApplier
{
    /// <summary>
    /// Synchronizes the game to <paramref name="loadoutId"/> and toasts the result. When a game launched from the
    /// app is still running, or the synchronizer refuses the apply up front (nothing was written), explains why in
    /// a dialog instead and returns false; any other failure propagates.
    /// </summary>
    public static async Task<bool> Apply(IServiceProvider serviceProvider, LoadoutId loadoutId)
    {
        var syncService = serviceProvider.GetRequiredService<ISynchronizerService>();
        var notificationService = serviceProvider.GetRequiredService<IWindowNotificationService>();
        var loadout = Sdk.Loadouts.Loadout.Load(serviceProvider.GetRequiredService<IConnection>().Db, loadoutId);

        try
        {
            return await MessageBoxOkViewModel.RunOrExplainRunningGame(serviceProvider, loadout.Installation.Name, async () =>
            {
                await Task.Run(() => syncService.Synchronize(loadoutId));
                notificationService.ShowToast(Language.ToastNotification_Applied__0__successfully, ToastNotificationVariant.Success);
            });
        }
        catch (SynchronizerPreflightException e)
        {
            // Left unhandled this only reaches the log, so the Apply button would just stop spinning.
            await ShowRefusal(serviceProvider, loadoutId, e.Message);
            return false;
        }
    }

    private static async Task ShowRefusal(IServiceProvider serviceProvider, LoadoutId loadoutId, string reason)
    {
        var markdown = serviceProvider.GetRequiredService<IMarkdownRendererViewModel>();
        markdown.Contents = reason;

        var dialog = DialogFactory.CreateStandardDialog(
            title: LoadoutApplierRefusal.Title,
            new StandardDialogParameters { Markdown = markdown },
            buttonDefinitions:
            [
                new DialogButtonDefinition("OK", ButtonDefinitionId.Accept, ButtonAction.Accept),
                new DialogButtonDefinition("Open Health Check", LoadoutApplierRefusal.OpenHealthCheck, ButtonAction.Accept, ButtonStyling.Primary),
            ]
        );

        var windowManager = serviceProvider.GetRequiredService<IWindowManager>();
        var result = await windowManager.ShowDialog(dialog, DialogWindowType.Modal);
        if (result.ButtonId == LoadoutApplierRefusal.OpenHealthCheck) OpenHealthCheck(windowManager, loadoutId);
    }

    private static void OpenHealthCheck(IWindowManager windowManager, LoadoutId loadoutId)
    {
        var pageData = new PageData
        {
            FactoryId = DiagnosticListPageFactory.StaticId,
            Context = new DiagnosticListPageContext { LoadoutId = loadoutId },
        };

        // The loadout's workspace may not be the one in front (the dialog is also offered from the loadout cards).
        // A workspace created here already opens the page, so it is opened the way the left menu's Health Check
        // item opens it; a forced new tab would show the Health Check twice.
        var controller = windowManager.ActiveWorkspaceController;
        var workspace = controller.ChangeOrCreateWorkspaceByContext<LoadoutContext>(
            context => context.LoadoutId == loadoutId,
            () => pageData,
            () => new LoadoutContext { LoadoutId = loadoutId }
        );
        var behavior = controller.GetOpenPageBehavior(pageData, NavigationInformation.From(NavigationInput.Default));
        controller.OpenPage(workspace.Id, pageData, behavior);
    }
}
