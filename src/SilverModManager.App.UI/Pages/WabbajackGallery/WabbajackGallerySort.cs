using SilverModManager.App.UI.Controls.Cards;
using SilverModManager.Wabbajack.Gallery;

namespace SilverModManager.App.UI.Pages.WabbajackGallery;

/// <summary>
/// How the Wabbajack page orders its lists. The gallery publishes no ratings or download counts, so only what it
/// does publish (dates, names, sizes) can be sorted on.
/// </summary>
public enum WabbajackGallerySort
{
    Updated,
    Newest,
    Name,
    LargestDownload,
    SmallestDownload,
}

public static class WabbajackGallerySorting
{
    public static IReadOnlyList<StoreSortOption> Choices { get; } =
    [
        new(nameof(WabbajackGallerySort.Updated), StoreSortLabels.RecentlyUpdated),
        new(nameof(WabbajackGallerySort.Newest), StoreSortLabels.Newest),
        new(nameof(WabbajackGallerySort.Name), StoreSortLabels.Name),
        new(nameof(WabbajackGallerySort.LargestDownload), StoreSortLabels.LargestDownload),
        new(nameof(WabbajackGallerySort.SmallestDownload), StoreSortLabels.SmallestDownload),
    ];

    /// <summary>
    /// Orders the lists. A list that never says when it changed counts from when it was created, and one with no size
    /// goes last in both size sorts: it has nothing to rank by, and "smallest" must not lead with lists of unknown size.
    /// Ties fall back to the title, then the list's id, so the order doesn't shuffle between refreshes.
    /// </summary>
    public static IReadOnlyList<GalleryEntry> Order(IEnumerable<GalleryEntry> entries, WabbajackGallerySort sort)
    {
        var ordered = sort switch
        {
            WabbajackGallerySort.Updated => entries.OrderByDescending(e => e.DateUpdated ?? e.DateCreated ?? DateTimeOffset.MinValue),
            WabbajackGallerySort.Newest => entries.OrderByDescending(e => e.DateCreated ?? DateTimeOffset.MinValue),
            WabbajackGallerySort.Name => entries.OrderBy(e => e.Title, StringComparer.CurrentCultureIgnoreCase),
            WabbajackGallerySort.LargestDownload => entries.OrderBy(e => e.ArchivesSize is null).ThenByDescending(e => e.ArchivesSize),
            WabbajackGallerySort.SmallestDownload => entries.OrderBy(e => e.ArchivesSize is null).ThenBy(e => e.ArchivesSize),
            _ => throw new ArgumentOutOfRangeException(nameof(sort), sort, null),
        };
        return (sort == WabbajackGallerySort.Name ? ordered : ordered.ThenBy(e => e.Title, StringComparer.CurrentCultureIgnoreCase))
            .ThenBy(e => e.MachineUrl, StringComparer.Ordinal)
            .ThenBy(e => e.Repository, StringComparer.Ordinal)
            .ToArray();
    }
}
