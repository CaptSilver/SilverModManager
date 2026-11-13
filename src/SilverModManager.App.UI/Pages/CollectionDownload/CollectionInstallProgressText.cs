using SilverModManager.App.UI.Resources;
using SilverModManager.Collections;

namespace SilverModManager.App.UI.Pages.CollectionDownload;

/// <summary>
/// The text the collection page shows for a <see cref="CollectionInstallProgress.Snapshot"/>.
/// </summary>
public static class CollectionInstallProgressText
{
    /// <summary>
    /// "Installing X of Y", counted over the whole collection so mods installed by earlier runs are included.
    /// </summary>
    public static string Summary(CollectionInstallProgress.Snapshot snapshot)
    {
        var done = snapshot.AlreadyInstalled + snapshot.Installed;
        var total = snapshot.AlreadyInstalled + snapshot.Total;
        var format = snapshot.IsFinished ? Language.CollectionInstallProgress_SummaryFinished : Language.CollectionInstallProgress_SummaryInstalling;
        return string.Format(format, done, total);
    }

    /// <summary>
    /// Says the collection stayed disabled because mods of this run failed, or an empty string otherwise.
    /// </summary>
    public static string DisabledNotice(CollectionInstallProgress.Snapshot snapshot)
    {
        // A run can also end disabled with no failures, e.g. when it only installed optional mods
        // while required ones are still missing; the notice must not blame failures then.
        return snapshot is { IsFinished: true, LeftDisabled: true, Failed: > 0 } ? Language.CollectionInstallProgress_LeftDisabled : string.Empty;
    }
}
