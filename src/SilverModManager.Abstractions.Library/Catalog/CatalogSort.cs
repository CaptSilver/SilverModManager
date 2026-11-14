namespace SilverModManager.Abstractions.Library.Catalog;

/// <summary>
/// One order a catalog can list in. <see cref="Key"/> is what the page remembers and the verbs take; <see cref="Label"/>
/// is what the sort box shows.
/// </summary>
public sealed record CatalogSort(string Key, string Label);

/// <summary>
/// The wording of the sort boxes, in one place so every store page names the same sort the same way. A store
/// picks "Top rated" or "Most endorsed" for rating, whichever it calls it.
/// </summary>
public static class CatalogSortLabels
{
    public const string TopRated = "Top rated";
    public const string MostEndorsed = "Most endorsed";
    public const string MostDownloaded = "Most downloaded";
    public const string RecentlyUpdated = "Recently updated";
    public const string Newest = "Newest";
    public const string Name = "Name";
    public const string LargestDownload = "Largest download";
    public const string SmallestDownload = "Smallest download";
}
