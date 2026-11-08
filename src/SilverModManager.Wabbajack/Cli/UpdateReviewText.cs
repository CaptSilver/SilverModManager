using SilverModManager.Wabbajack.Update;

namespace SilverModManager.Wabbajack.Cli;

/// <summary>The lines <c>wabbajack update</c> prints for a review, kept apart from the verb so they can be tested without a renderer.</summary>
internal static class UpdateReviewText
{
    /// <summary>Said under the review: what applying the update does first.</summary>
    public const string RestorePointNote = "A restore point is written first; Undo is on the modlist page.";

    /// <summary>The heading of the mods and plugins the user changed and the list changes to something else.</summary>
    public const string KeptYoursHeading = "Your edits the list overrides — kept yours";

    public static string Header(string name, string installed, string newer, ModlistUpdatePreviewResult review)
        => $"A newer version of {name} is available ({installed} → {newer}, {review.Downloads.Count} archives to download, {review.DownloadSize})";

    /// <summary>
    /// The review as the update dialog counts it. Added, changed, renamed and removed folders always have a line; the
    /// others only when there is something to say. With <paramref name="listItems"/> every count is followed by what it counts.
    /// </summary>
    public static IReadOnlyList<string> Describe(ModlistUpdatePreviewResult review, bool listItems)
    {
        var lines = new List<string>();

        void Section(string title, IReadOnlyCollection<string> items, bool always)
        {
            if (items.Count == 0 && !always) return;
            lines.Add($"{title}: {items.Count}");
            if (listItems) lines.AddRange(items.Select(item => $"  {item}"));
        }

        Section("Mods added", review.Added, always: true);
        Section("Mods changed", review.Changed, always: true);
        Section("Mods renamed", review.Renamed.Select(r => $"{r.From} → {r.To}").ToArray(), always: true);
        Section("Mods removed", review.Removed, always: true);
        Section("Kept mods the list moves or switches", review.ModOrder, always: false);
        Section(KeptYoursHeading, review.Conflicts.Concat(review.Plugins).ToArray(), always: false);
        Section("Textures retried", review.RetriedTextures, always: false);
        Section("Game files kept", review.GameFilesKept.Select(path => path.ToString()).ToArray(), always: false);
        Section("Your INI tweaks kept", review.UserTweaksKept.Select(path => path.ToString()).ToArray(), always: false);
        if (listItems && review.Downloads.Count > 0)
        {
            lines.Add("Archives to download:");
            lines.AddRange(review.Downloads.Select(row => $"  {row.Archive.Name}"));
        }

        lines.AddRange(review.Warnings);
        return lines;
    }
}
