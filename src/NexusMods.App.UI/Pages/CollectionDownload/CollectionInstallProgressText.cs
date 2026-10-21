using NexusMods.App.UI.Resources;
using NexusMods.Collections;

namespace NexusMods.App.UI.Pages.CollectionDownload;

/// <summary>
/// The text the collection page shows for a <see cref="CollectionInstallProgress.Snapshot"/>.
/// </summary>
public static class CollectionInstallProgressText
{
    // A 287-mod install runs many mods at once; past this the list stops being readable.
    private const int MaxNamesShown = 5;

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
    /// The mods running right now, or an empty string if none are.
    /// </summary>
    public static string Installing(CollectionInstallProgress.Snapshot snapshot)
    {
        if (snapshot.Installing.Count == 0) return string.Empty;

        var names = string.Join(", ", snapshot.Installing.Take(MaxNamesShown));
        var more = snapshot.Installing.Count - MaxNamesShown;
        return more > 0
            ? string.Format(Language.CollectionInstallProgress_InstallingWithMore, names, more)
            : string.Format(Language.CollectionInstallProgress_Installing, names);
    }

    /// <summary>
    /// One line per failed mod: its name and why it failed.
    /// </summary>
    public static IReadOnlyList<string> FailedLines(CollectionInstallProgress.Snapshot snapshot)
    {
        return snapshot.Failed.Select(failed => string.Format(Language.CollectionInstallProgress_FailedMod, failed.Name, failed.Reason)).ToArray();
    }

    /// <summary>
    /// Says the collection stayed disabled because mods of this run failed, or an empty string otherwise.
    /// </summary>
    public static string DisabledNotice(CollectionInstallProgress.Snapshot snapshot)
    {
        // A run can also end disabled with no failures, e.g. when it only installed optional mods
        // while required ones are still missing; the notice must not blame failures then.
        return snapshot is { IsFinished: true, LeftDisabled: true, Failed.Count: > 0 } ? Language.CollectionInstallProgress_LeftDisabled : string.Empty;
    }
}
