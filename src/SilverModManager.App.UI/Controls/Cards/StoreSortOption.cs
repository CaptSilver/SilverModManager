namespace SilverModManager.App.UI.Controls.Cards;

/// <summary>
/// One entry of a store page's sort box. <see cref="Key"/> is what the page stores and reads back (the name of
/// its sort enum member); <see cref="Label"/> is what the user sees.
/// </summary>
public sealed record StoreSortOption(string Key, string Label);

/// <summary>
/// The wording of the sort boxes, in one place so every store page names the same sort the same way. A store
/// picks "Top rated" or "Most endorsed" for rating, whichever it calls it.
/// </summary>
public static class StoreSortLabels
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
