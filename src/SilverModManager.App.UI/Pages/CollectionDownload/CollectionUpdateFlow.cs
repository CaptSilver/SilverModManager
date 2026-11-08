using SilverModManager.Sdk.Errors;
using SilverModManager.App.UI.Controls.DragAndDrop;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Collections;
using SilverModManager.App.UI.Dialog;
using SilverModManager.App.UI.Dialog.Enums;
using SilverModManager.App.UI.Pages.Collections.Update;
using SilverModManager.App.UI.Resources;
using SilverModManager.App.UI.Windows;
using SilverModManager.Collections;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Jobs;
using SilverModManager.UI.Sdk;
using SilverModManager.UI.Sdk.Dialog;
using SilverModManager.UI.Sdk.Dialog.Enums;
using Microsoft.Extensions.DependencyInjection;

namespace SilverModManager.App.UI.Pages.CollectionDownload;

/// <summary>
/// Drives an <see cref="ICollectionUpdate"/> from the review to the toast: the user's go-ahead, the download, the update
/// itself, the question about dropped mods and the error report.
/// </summary>
internal static class CollectionUpdateFlow
{
    /// <summary>What the toast after an update says.</summary>
    public static string ResultText(CollectionUpdateResult result, int revision = 0)
    {
        if (result.Error is not null) return string.Format(Language.ToastNotification_Collection_update_interrupted, revision, MarkdownText.Message(result.Error));

        var text = result.Failed.Count == 0
            ? Language.ToastNotification_Collection_updated
            : string.Format(Language.ToastNotification_Collection_updated_with_failures, ModCount.Text(result.Failed.Count));
        if (result.Failed.Count == 0 && (result.SkippedRules.Count > 0 || result.Pending.Count > 0)) text += ".";
        if (result.Pending.Count > 0)
            text += result.Pending.Count == 1 ? Language.ToastNotification_Collection_update_one_needs_review : string.Format(Language.ToastNotification_Collection_update_need_review, result.Pending.Count);
        if (result.OldVersionsKept > 0) text += Language.ToastNotification_Collection_update_kept_old_versions;
        if (result.SkippedRules.Count > 0) text += string.Format(Language.ToastNotification_Collection_update_skipped_rules, result.SkippedRules.Count);
        return text;
    }

    internal static readonly ButtonDefinitionId UndoId = ButtonDefinitionId.From("undo-update");

    /// <summary>
    /// One press of "Update to…": the review, the user's go-ahead, the download if the library lacks something, the
    /// update itself and the toast. What differs between sources lives behind <see cref="ICollectionUpdate"/>.
    /// </summary>
    public static async Task Run(
        IServiceProvider serviceProvider,
        IWindowManager windowManager,
        IWindowNotificationService notifications,
        ICollectionUpdate update,
        CancellationToken cancellationToken)
    {
        var logger = serviceProvider.GetRequiredService<ILogger<CollectionDownloadViewModel>>();
        try
        {
            // The review can fetch the new list and hash archives; the progress dialog shows that and can stop it.
            var checkedUpdate = await CollectionUpdateProgressDialog.Run(
                windowManager,
                string.Format(Language.CollectionUpdate_Menu_UpdateTo, update.NewVersionText),
                Language.CollectionUpdate_Progress_Checking,
                async (progress, ct) => new ReviewedUpdate(await update.Review(progress, ct), await update.IsDownloaded(ct)),
                cancellationToken);
            if (checkedUpdate is null) return;

            var (review, isDownloaded) = checkedUpdate;
            var (dialog, choices) = CollectionUpdateReviewDialog.Create(update, review, isDownloaded);
            var answer = await windowManager.ShowDialog(dialog, DialogWindowType.Modal);
            if (answer.ButtonId != ButtonDefinitionId.Accept) return;

            if (!isDownloaded)
            {
                switch (await update.Download(cancellationToken))
                {
                    case CollectionUpdateDownload.CantDownload:
                        // The source already said why (login, premium).
                        return;
                    case CollectionUpdateDownload.NotAllDownloaded:
                        notifications.ShowToast(string.Format(Language.CollectionUpdate_NotAllDownloaded, MarkdownText.InlineMidLine(update.Name), MarkdownText.InlineMidLine(update.NewVersionText)), ToastNotificationVariant.Failure);
                        return;
                }
            }

            var outcome = await update.Apply(choices.SkipMissing, cancellationToken);
            var succeeded = outcome is { Error: null, Failed: 0, Warnings: 0 };
            if (outcome.Error is not null) logger.LogError(outcome.Error, "Updating `{Name}` to {To} stopped early", update.Name, update.NewVersionText);
            notifications.ShowToast(outcome.ToastText, succeeded ? ToastNotificationVariant.Success : ToastNotificationVariant.Failure);

            if (outcome is { Error: not null, Undo: { } undo } && await OfferUndo(windowManager, notifications, logger, update, outcome, undo)) return;

            var toRemove = outcome.DroppedPromptRevision is { } revision
                ? await DroppedModsPrompt.Ask(windowManager, update.Name, revision, outcome.DroppedToAsk)
                : await DroppedModsPrompt.Ask(windowManager, update.Name, update.NewVersionText, outcome.DroppedToAsk);
            if (toRemove.Count > 0) await CollectionUpdater.RemoveDropped(serviceProvider, toRemove);
        }
        catch (Exception e) when (!TimeoutDetection.IsRealCancellation(e))
        {
            // Not rethrown: the commands that call this have no error handler, so a throw would swallow the explanation.
            logger.LogError(e, "Updating `{Name}` to {To} failed", update.Name, update.NewVersionText);
            notifications.ShowToast(string.Format(Language.CollectionUpdate_Failed, MarkdownText.InlineMidLine(update.Name), MarkdownText.InlineMidLine(update.NewVersionText), MarkdownText.Message(e)), ToastNotificationVariant.Failure);
        }
    }

    private sealed record ReviewedUpdate(CollectionUpdateReview Review, bool IsDownloaded);

    /// <summary>
    /// The update stopped after writing a restore point. A dialog (a control that always works, unlike a toast button)
    /// offers to put the list back. Returns whether the user undid it.
    /// </summary>
    private static async Task<bool> OfferUndo(
        IWindowManager windowManager,
        IWindowNotificationService notifications,
        ILogger logger,
        ICollectionUpdate update,
        CollectionUpdateOutcome outcome,
        Func<CancellationToken, Task> undo)
    {
        var dialog = DialogFactory.CreateStandardDialog(
            Language.CollectionUpdate_UndoOffer_Title,
            new StandardDialogParameters { Text = string.Format(Language.CollectionUpdate_UndoOffer_Text, MarkdownText.ToPlain(outcome.ToastText), update.Name) },
            [
                DialogStandardButtons.Close,
                new DialogButtonDefinition(Language.CollectionUpdate_Undo, UndoId, ButtonAction.Accept, ButtonStyling.Primary),
            ]
        );
        var answer = await windowManager.ShowDialog(dialog, DialogWindowType.Modal);
        if (answer.ButtonId != UndoId) return false;

        try
        {
            await undo(CancellationToken.None);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Undoing the update of `{Name}` to {To} failed", update.Name, update.NewVersionText);
            notifications.ShowToast(MarkdownText.Message(e), ToastNotificationVariant.Failure);
            return false;
        }

        notifications.ShowToast(string.Format(Language.CollectionUpdate_Undone, MarkdownText.InlineMidLine(update.Name), MarkdownText.InlineMidLine(update.CurrentVersionText)), ToastNotificationVariant.Success);
        return true;
    }
}
