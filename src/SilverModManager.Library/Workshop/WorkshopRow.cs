namespace SilverModManager.Library.Workshop;

/// <summary>
/// One Workshop item the user is subscribed to, as the Workshop page and the group on the Load Order page show it.
/// </summary>
/// <param name="Id">The published file id, which is also the name of the item's folder.</param>
/// <param name="Title">What Steam calls the item; its id when Steam could not be asked or no longer lists it.</param>
/// <param name="Updated">When the author last updated the item, or null when Steam did not say.</param>
/// <param name="FolderMtime">When the item's folder last changed on disk, in Unix seconds.</param>
/// <param name="UpdatedBySteam">The author updated the item after the folder last changed, so Steam has a newer copy to download or has just done so.</param>
public sealed record WorkshopRow(ulong Id, string Title, DateTimeOffset? Updated, long FolderMtime, bool UpdatedBySteam)
{
    /// <summary>The item's page on the Workshop site, which opens in any browser.</summary>
    public Uri WebPage => SteamWorkshopLinks.WebPage(Id);

    /// <summary>The link that makes Steam show the item, where the user subscribes.</summary>
    public Uri SubscribeLink => SteamWorkshopLinks.Subscribe(Id);
}

/// <summary>
/// The two addresses an item has.
/// </summary>
public static class SteamWorkshopLinks
{
    /// <summary>
    /// <c>steam://</c> goes to whatever handles the scheme and does nothing when there is none, so this is the link the
    /// page offers beside <see cref="WebPage"/>.
    /// </summary>
    public static Uri Subscribe(ulong id) => new($"steam://url/CommunityFilePage/{id}");

    /// <summary>The item's page on steamcommunity.com.</summary>
    public static Uri WebPage(ulong id) => new($"https://steamcommunity.com/sharedfiles/filedetails/?id={id}");
}
