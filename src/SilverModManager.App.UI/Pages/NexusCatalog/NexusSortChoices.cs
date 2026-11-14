using SilverModManager.Abstractions.Library.Catalog;
using SilverModManager.App.UI.Controls.Cards;
using SilverModManager.Networking.NexusWebApi;

namespace SilverModManager.App.UI.Pages.NexusCatalog;

/// <summary>The sorts the Nexus Mods page offers on each tab, in the shared wording; Nexus calls rating endorsements.</summary>
public static class NexusSortChoices
{
    public static IReadOnlyList<StoreSortOption> Mods { get; } =
    [
        new(nameof(NexusModsSort.Endorsements), CatalogSortLabels.MostEndorsed),
        new(nameof(NexusModsSort.Downloads), CatalogSortLabels.MostDownloaded),
        new(nameof(NexusModsSort.Updated), CatalogSortLabels.RecentlyUpdated),
        new(nameof(NexusModsSort.Name), CatalogSortLabels.Name),
    ];

    public static IReadOnlyList<StoreSortOption> Collections { get; } =
    [
        new(nameof(NexusCollectionsSort.Endorsements), CatalogSortLabels.MostEndorsed),
        new(nameof(NexusCollectionsSort.Downloads), CatalogSortLabels.MostDownloaded),
        new(nameof(NexusCollectionsSort.Updated), CatalogSortLabels.RecentlyUpdated),
    ];
}
