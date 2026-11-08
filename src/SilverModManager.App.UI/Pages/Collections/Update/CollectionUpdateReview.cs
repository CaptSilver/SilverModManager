using NexusMods.Paths;

namespace SilverModManager.App.UI.Pages.Collections.Update;

/// <summary>One archive the update has to download. <paramref name="NeedsBrowser"/>: it cannot be fetched automatically.</summary>
public sealed record ReviewArchive(string Name, Size Size, string Source, bool NeedsBrowser);

public sealed record ReviewRename(string From, string To);

/// <summary>
/// What an update would change, as plain text the review dialog lists. Built without writing anything.
/// </summary>
/// <param name="DiskNote">The sentence about free space, shown under the download size.</param>
/// <param name="WritesRestorePoint">Whether applying writes a restore point first, so the dialog says so.</param>
/// <param name="DroppedAreRemovedNotAsked">Whether the mods in <paramref name="Dropped"/> go without a question afterwards.</param>
public sealed record CollectionUpdateReview(
    IReadOnlyList<ReviewArchive> Downloads,
    Size DownloadSize,
    Size? NewBytesUpperBound,
    string DiskNote,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Changed,
    IReadOnlyList<ReviewRename> Renamed,
    IReadOnlyList<string> Dropped,
    IReadOnlyList<string> ModOrderChanges,
    IReadOnlyList<string> PluginChanges,
    IReadOnlyList<string> UserEditsKept,
    IReadOnlyList<string> GameFilesKept,
    IReadOnlyList<string> Warnings,
    bool WritesRestorePoint,
    bool DroppedAreRemovedNotAsked);
