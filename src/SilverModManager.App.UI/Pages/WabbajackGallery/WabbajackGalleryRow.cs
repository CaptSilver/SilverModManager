using NexusMods.Paths;
using SilverModManager.Sdk.Library;
using SilverModManager.Wabbajack.Gallery;

namespace SilverModManager.App.UI.Pages.WabbajackGallery;

/// <summary>
/// One gallery list as the page shows it. <paramref name="LibraryFile"/> is the list's file when it is in the
/// library already, which turns Install into Open.
/// </summary>
public sealed record WabbajackGalleryRow(GalleryEntry Entry, LibraryFileId? LibraryFile = null)
{
    /// <summary>The tags a card has room for; the rest are still searchable.</summary>
    public const int MaxShownTags = 3;

    public string Title => Entry.Title;

    /// <summary>"by Mara", or nothing for a list with no author.</summary>
    public string AuthorLine => Entry.Author == "" ? "" : $"by {Entry.Author}";

    public string VersionLine => Entry.Version == "" ? "" : $"Version {Entry.Version}";

    public string Description => Entry.Description;

    public bool IsOfficial => Entry.IsOfficial;

    public IReadOnlyList<string> ShownTags => Entry.Tags.Take(MaxShownTags).ToArray();

    public Uri? ImageUrl => Entry.Image;

    public Uri? ReadmeUrl => Entry.Readme;

    public Uri? WebsiteUrl => Entry.Website;

    /// <summary>"download 85 GB · installed 210 GB", the sizes the author published; only what is known is named.</summary>
    public string SizesLine
    {
        get
        {
            var parts = new List<string>(2);
            if (Entry.ArchivesSize is { } download) parts.Add($"download {Size.From(download)}");
            if (Entry.InstalledSize is { } installed) parts.Add($"installed {Size.From(installed)}");
            return string.Join(" · ", parts);
        }
    }

    public bool IsInLibrary => LibraryFile is not null;

    public string InstallButtonText => IsInLibrary ? "Open" : "Install";

    /// <summary>A list can be opened from the library, or downloaded when the gallery gave what its file is checked against.</summary>
    public bool CanInstall => IsInLibrary || Entry.IsDownloadable;
}
