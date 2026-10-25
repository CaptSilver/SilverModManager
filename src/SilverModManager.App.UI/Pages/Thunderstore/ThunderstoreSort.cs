using SilverModManager.App.UI.Controls.Cards;
using SilverModManager.Networking.Thunderstore.DTOs;

namespace SilverModManager.App.UI.Pages.Thunderstore;

/// <summary>How the Thunderstore page orders the packages it lists.</summary>
public enum ThunderstoreSort
{
    TopRated,
    Downloads,
    Updated,
    Newest,
    Name,
}

public static class ThunderstoreSorting
{
    public static IReadOnlyList<StoreSortOption> Choices { get; } =
    [
        new(nameof(ThunderstoreSort.TopRated), StoreSortLabels.TopRated),
        new(nameof(ThunderstoreSort.Downloads), StoreSortLabels.MostDownloaded),
        new(nameof(ThunderstoreSort.Updated), StoreSortLabels.RecentlyUpdated),
        new(nameof(ThunderstoreSort.Newest), StoreSortLabels.Newest),
        new(nameof(ThunderstoreSort.Name), StoreSortLabels.Name),
    ];

    /// <summary>
    /// Orders the packages. Pinned packages lead only under <see cref="ThunderstoreSort.TopRated"/>, as on thunderstore.io;
    /// any other sort asked for a different order and gets it. Ties fall back to the readable name, then the full name,
    /// so the order doesn't shuffle between refreshes.
    /// </summary>
    public static IReadOnlyList<PackageListing> Order(IEnumerable<PackageListing> packages, ThunderstoreSort sort)
    {
        var ordered = sort switch
        {
            ThunderstoreSort.TopRated => packages.OrderByDescending(p => p.IsPinned).ThenByDescending(p => p.RatingScore),
            ThunderstoreSort.Downloads => packages.OrderByDescending(Downloads),
            ThunderstoreSort.Updated => packages.OrderByDescending(p => p.DateUpdated ?? DateTimeOffset.MinValue),
            ThunderstoreSort.Newest => packages.OrderByDescending(p => p.DateCreated ?? DateTimeOffset.MinValue),
            ThunderstoreSort.Name => packages.OrderBy(NameOf, StringComparer.CurrentCultureIgnoreCase),
            _ => throw new ArgumentOutOfRangeException(nameof(sort), sort, null),
        };
        // The primary key of the Name sort is already the name.
        return (sort == ThunderstoreSort.Name ? ordered : ordered.ThenBy(NameOf, StringComparer.CurrentCultureIgnoreCase))
            .ThenBy(p => p.FullName, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>The downloads of every version added up, the figure the card shows.</summary>
    public static long Downloads(PackageListing package) => package.Versions.Sum(version => version.Downloads);

    /// <summary>The readable title, falling back to the full name like the card does.</summary>
    public static string NameOf(PackageListing package)
    {
        var name = ThunderstoreRowText.DisplayName(package.Name, package.Owner);
        return name != "" ? name : package.FullName;
    }
}
