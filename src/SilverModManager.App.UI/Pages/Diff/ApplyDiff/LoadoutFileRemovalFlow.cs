using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.App.UI.Dialog;
using SilverModManager.App.UI.Dialog.Enums;
using SilverModManager.App.UI.Resources;
using SilverModManager.App.UI.Windows;
using SilverModManager.UI.Sdk;
using SilverModManager.UI.Sdk.Dialog;
using SilverModManager.UI.Sdk.Dialog.Enums;

namespace SilverModManager.App.UI.Pages.Diff.ApplyDiff;

/// <summary>
/// Plans a file removal, asks the user, removes, and tells them. The Apply preview's Delete, its stray-file notice and
/// the Health Check's delete button all run it, so they ask the same question and fail the same way.
/// </summary>
public static class LoadoutFileRemovalFlow
{
    /// <summary>
    /// Plans at the call rather than earlier: the database moves between a click and the one before it. Every failure,
    /// a refusal included, goes to the error presenter and nothing is rethrown, since an exception out of a command lands
    /// in a stream nobody listens to.
    /// </summary>
    /// <param name="activity">What the error presenter says was being done when a failure happened.</param>
    /// <returns>True when the rows were removed; false when the user cancelled or something failed.</returns>
    public static async Task<bool> Run(IServiceProvider serviceProvider, IWindowManager windowManager, FileRemovalRequest request, string activity)
    {
        var remover = serviceProvider.GetRequiredService<ILoadoutFileRemover>();
        try
        {
            var plan = await Task.Run(() => remover.Plan(request));
            if (!plan.HasAnythingToRemove(includeLocked: true))
                throw new FileRemovalRefusedException(plan.RefusalMessage());

            var dialog = DialogFactory.CreateStandardDialog(
                Language.ApplyDiff_Remove_Title,
                new StandardDialogParameters { Text = FileRemovalText.For(plan) },
                [
                    DialogStandardButtons.Cancel,
                    new DialogButtonDefinition(Language.ApplyDiff_Remove_Confirm, ButtonDefinitionId.Accept, ButtonAction.Accept, ButtonStyling.Destructive),
                ]);
            var answer = await windowManager.ShowDialog(dialog, DialogWindowType.Modal);
            if (answer.ButtonId != ButtonDefinitionId.Accept) return false;

            await Task.Run(() => remover.Remove(request, includeLocked: true));
            serviceProvider.GetRequiredService<IWindowNotificationService>().ShowToast(Language.ApplyDiff_Remove_Toast);
            return true;
        }
        catch (Exception e)
        {
            await serviceProvider.GetRequiredService<IErrorPresenter>().Report(e, activity);
            return false;
        }
    }
}
