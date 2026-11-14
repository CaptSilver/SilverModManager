using SilverModManager.Abstractions.Library.Catalog;
using SilverModManager.Networking.Thunderstore;
using SilverModManager.Networking.Thunderstore.DTOs;

namespace SilverModManager.Library;

/// <summary>
/// How the Thunderstore catalog orders the packages it lists.
/// </summary>
internal static class ThunderstoreSorts
{
    public const string TopRated = nameof(TopRated);
    public const string Downloads = nameof(Downloads);
    public const string Updated = nameof(Updated);
    public const string Newest = nameof(Newest);
    public const string Name = nameof(Name);

    public static IReadOnlyList<CatalogSort> Choices { get; } =
    [
        new(TopRated, CatalogSortLabels.TopRated),
        new(Downloads, CatalogSortLabels.MostDownloaded),
        new(Updated, CatalogSortLabels.RecentlyUpdated),
        new(Newest, CatalogSortLabels.Newest),
        new(Name, CatalogSortLabels.Name),
    ];

    /// <summary>
    /// Orders the packages. Pinned packages lead only under <see cref="TopRated"/>, as on thunderstore.io; any other
    /// sort asked for a different order and gets it. Ties fall back to the readable name, then the full name, so the
    /// order doesn't shuffle between refreshes.
    /// </summary>
    public static IReadOnlyList<PackageListing> Order(IEnumerable<PackageListing> packages, string sort)
    {
        var ordered = sort switch
        {
            TopRated => packages.OrderByDescending(p => p.IsPinned).ThenByDescending(p => p.RatingScore),
            Downloads => packages.OrderByDescending(TotalDownloads),
            Updated => packages.OrderByDescending(p => p.DateUpdated ?? DateTimeOffset.MinValue),
            Newest => packages.OrderByDescending(p => p.DateCreated ?? DateTimeOffset.MinValue),
            Name => packages.OrderBy(NameOf, StringComparer.CurrentCultureIgnoreCase),
            _ => throw new ArgumentOutOfRangeException(nameof(sort), sort, "Not a Thunderstore sort."),
        };
        // The primary key of the Name sort is already the name.
        return (sort == Name ? ordered : ordered.ThenBy(NameOf, StringComparer.CurrentCultureIgnoreCase))
            .ThenBy(p => p.FullName, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// The downloads of every version added up, the figure the card shows.
    /// </summary>
    public static long TotalDownloads(PackageListing package) => package.Versions.Sum(version => version.Downloads);

    /// <summary>
    /// The readable title, falling back to the full name like the card does.
    /// </summary>
    public static string NameOf(PackageListing package)
    {
        var name = ThunderstoreRowText.DisplayName(package.Name, package.Owner);
        return name != "" ? name : package.FullName;
    }
}
