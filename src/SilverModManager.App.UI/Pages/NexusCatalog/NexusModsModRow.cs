using SilverModManager.App.UI.Pages.Thunderstore;
using SilverModManager.Networking.NexusWebApi;

namespace SilverModManager.App.UI.Pages.NexusCatalog;

/// <summary>One mod of the catalog as the Nexus Mods page lists it.</summary>
public sealed record NexusModsModRow(NexusModsSearchHit Hit)
{
    public string Name => Hit.Name;
    public string Summary => Hit.Summary;

    /// <summary>"by someone", or nothing when the mod credits no one.</summary>
    public string AuthorLine => Hit.Author == "" ? "" : $"by {Hit.Author}";

    /// <summary>"1.0  ·  1.2K endorsements  ·  1.5M downloads", the small print under the title.</summary>
    public string Details => $"{Hit.Version}  ·  {ThunderstoreRowText.CompactCount(Hit.Endorsements)} endorsements  ·  {ThunderstoreRowText.CompactCount(Hit.Downloads)} downloads";

    /// <summary>The small image for a card; the full picture stands in for a mod without a thumbnail.</summary>
    public Uri? ThumbnailUrl => Hit.ThumbnailUrl ?? Hit.PictureUrl;
}
