using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.Sdk.Errors;
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

            var result = await Task.Run(() => remover.Remove(request, includeLocked: true, takeOffDisk: true));
            await ApplyAfterRemoval(serviceProvider, request, result, activity);
            return true;
        }
        catch (Exception e)
        {
            await serviceProvider.GetRequiredService<IErrorPresenter>().Report(e, activity);
            return false;
        }
    }

    /// <summary>
    /// The files are off disk, so the loadout has to be applied before anything else can happen: a revert or an
    /// enable between the two would turn the missing file into a recorded delete. A failed apply does not undo the
    /// removal, so it is reported here and the caller still refreshes.
    /// </summary>
    private static async Task ApplyAfterRemoval(IServiceProvider serviceProvider, FileRemovalRequest request, FileRemovalResult result, string activity)
    {
        var toasts = serviceProvider.GetRequiredService<IWindowNotificationService>();
        var removed = result.Removed.Count == 1 ? "1 file" : $"{result.Removed.Count} files";
        try
        {
            var syncService = serviceProvider.GetRequiredService<ISynchronizerService>();
            using var origin = SyncOrigin.Begin(SyncTrigger.RemoveFiles);
            // Builds the whole sync tree, so it stays off the UI thread.
            await Task.Run(() => syncService.Synchronize(request.LoadoutId));
        }
        catch (UserFacingException e)
        {
            toasts.ShowToast(string.Format(Language.ApplyDiff_Remove_ApplyFailedToast, removed) + " " + e.Message, ToastNotificationVariant.Failure);
            return;
        }
        catch (Exception e)
        {
            toasts.ShowToast(string.Format(Language.ApplyDiff_Remove_ApplyFailedToast, removed), ToastNotificationVariant.Failure);
            await serviceProvider.GetRequiredService<IErrorPresenter>().Report(e, activity);
            return;
        }

        var trashed = result.OffDisk?.Trashed.Count ?? 0;
        toasts.ShowToast(string.Format(Language.ApplyDiff_Remove_Toast, removed, trashed > 0 ? $" ({trashed} to the trash)" : ""));
    }
}
