using SilverModManager.App.UI.Controls.Cards;
using SilverModManager.Networking.NexusWebApi;

namespace SilverModManager.App.UI.Pages.NexusCatalog;

/// <summary>The sorts the Nexus Mods page offers on each tab, in the shared wording; Nexus calls rating endorsements.</summary>
public static class NexusSortChoices
{
    public static IReadOnlyList<StoreSortOption> Mods { get; } =
    [
        new(nameof(NexusModsSort.Endorsements), StoreSortLabels.MostEndorsed),
        new(nameof(NexusModsSort.Downloads), StoreSortLabels.MostDownloaded),
        new(nameof(NexusModsSort.Updated), StoreSortLabels.RecentlyUpdated),
        new(nameof(NexusModsSort.Name), StoreSortLabels.Name),
    ];

    public static IReadOnlyList<StoreSortOption> Collections { get; } =
    [
        new(nameof(NexusCollectionsSort.Endorsements), StoreSortLabels.MostEndorsed),
        new(nameof(NexusCollectionsSort.Downloads), StoreSortLabels.MostDownloaded),
        new(nameof(NexusCollectionsSort.Updated), StoreSortLabels.RecentlyUpdated),
    ];
}
