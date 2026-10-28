using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Collections;
using SilverModManager.Abstractions.NexusModsLibrary.Models;
using SilverModManager.App.UI.Resources;
using SilverModManager.App.UI.Windows;
using SilverModManager.Collections;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Jobs;
using SilverModManager.UI.Sdk;
using Microsoft.Extensions.DependencyInjection;

namespace SilverModManager.App.UI.Pages.CollectionDownload;

/// <summary>
/// Updates the installed collection to a revision and tells the user how it went. The work is
/// <see cref="CollectionUpdater"/>; this adds the toast, the question about the dropped mods and the error report.
/// </summary>
internal static class CollectionUpdateFlow
{
    /// <summary>What the toast after an update says.</summary>
    public static string ResultText(CollectionUpdateResult result, int revision = 0)
    {
        if (result.Error is not null) return string.Format(Language.ToastNotification_Collection_update_interrupted, revision, result.Error.Message);

        var text = result.Failed.Count == 0
            ? Language.ToastNotification_Collection_updated
            : string.Format(Language.ToastNotification_Collection_updated_with_failures, MainWindowDropPlan.ModCount(result.Failed.Count));
        if (result.Failed.Count == 0 && result.SkippedRules.Count > 0) text += ".";
        if (result.OldVersionsKept > 0) text += Language.ToastNotification_Collection_update_kept_old_versions;
        if (result.SkippedRules.Count > 0) text += string.Format(Language.ToastNotification_Collection_update_skipped_rules, result.SkippedRules.Count);
        return text;
    }

    public static async Task Run(
        IServiceProvider serviceProvider,
        IWindowManager windowManager,
        IWindowNotificationService notifications,
        NexusCollectionLoadoutGroup.ReadOnly group,
        CollectionRevisionMetadata.ReadOnly revision,
        CancellationToken cancellationToken)
    {
        var logger = serviceProvider.GetRequiredService<ILogger<CollectionDownloadViewModel>>();
        try
        {
            var result = await CollectionUpdater.Update(serviceProvider, group, revision, cancellationToken);
            var succeeded = result.Error is null && result.Failed.Count == 0 && result.SkippedRules.Count == 0;
            notifications.ShowToast(ResultText(result, (int)revision.RevisionNumber.Value), succeeded ? ToastNotificationVariant.Success : ToastNotificationVariant.Failure);
            if (result.Error is not null) logger.LogError(result.Error, "Updating collection `{Slug}` to revision {To} stopped early", revision.Collection.Slug, revision.RevisionNumber);

            var toRemove = await DroppedModsPrompt.Ask(windowManager, revision.Collection.Name, (int)revision.RevisionNumber.Value, result.Dropped);
            if (toRemove.Count > 0) await CollectionUpdater.RemoveDropped(serviceProvider, toRemove);
        }
        catch (Exception e) when (!TimeoutDetection.IsRealCancellation(e))
        {
            // Not rethrown: the commands that call this have no error handler, so a throw would swallow the explanation.
            logger.LogError(e, "Updating collection `{Slug}` to revision {To} failed", revision.Collection.Slug, revision.RevisionNumber);
            notifications.ShowToast(
                string.Format(Language.ToastNotification_Couldn_t_update_to_revision__0____1_, revision.RevisionNumber, e.Message),
                ToastNotificationVariant.Failure
            );
        }
    }
}
