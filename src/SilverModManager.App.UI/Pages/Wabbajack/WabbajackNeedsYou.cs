using SilverModManager.Wabbajack.Acquire;

namespace SilverModManager.App.UI.Pages.Wabbajack;

/// <summary>Which archives the table lists.</summary>
public enum WabbajackArchiveFilter
{
    All,

    /// <summary>The archives the user has to deal with; see <see cref="WabbajackNeedsYou.Needs"/>.</summary>
    NeedsYou,

    /// <summary>Everything that is not in the library, game files included.</summary>
    Missing,

    InLibrary,
}

/// <summary>Which archives of a modlist the user has to deal with, how they are ordered, and the text that lists them.</summary>
public static class WabbajackNeedsYou
{
    /// <summary>
    /// The user fetches the file, signs in for it, or has to look at why it failed: a row waiting for a browser,
    /// forum or manual download, or an automatic one that carries a reason because its download did not work.
    /// </summary>
    public static bool Needs(ArchivePlanRow row)
        => row.State.WaitsForUser() || (row.State == ArchiveState.Automatic && !string.IsNullOrEmpty(row.Reason));

    /// <summary>"1 archive needs you" or "3 archives need you".</summary>
    public static string Heading(int count) => count == 1 ? "1 archive needs you" : $"{count} archives need you";

    public static bool Matches(WabbajackArchiveFilter filter, ArchivePlanRow row) => filter switch
    {
        WabbajackArchiveFilter.All => true,
        WabbajackArchiveFilter.NeedsYou => Needs(row),
        WabbajackArchiveFilter.Missing => row.State != ArchiveState.Have,
        WabbajackArchiveFilter.InLibrary => row.State == ArchiveState.Have,
        _ => throw new ArgumentOutOfRangeException(nameof(filter), filter, "Unknown archive filter"),
    };

    /// <summary>By source, so the files of one site sit together, then by name. A row's source and name never change.</summary>
    public static IComparer<WabbajackArchiveRow> Order { get; } = Comparer<WabbajackArchiveRow>.Create((a, b) =>
    {
        var bySource = StringComparer.OrdinalIgnoreCase.Compare(a.Source, b.Source);
        return bySource != 0 ? bySource : StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
    });

    /// <summary>"file name — page address or 'no page' — why it needs the user".</summary>
    public static string CopyLine(WabbajackArchiveRow row)
        => $"{row.Name} — {row.Plan.Page?.AbsoluteUri ?? "no page"} — {(row.Reason == "" ? row.StateText : row.Reason)}";

    /// <summary>One <see cref="CopyLine"/> per row, plain text.</summary>
    public static string CopyText(IEnumerable<WabbajackArchiveRow> rows) => string.Join("\n", rows.Select(CopyLine));
}
