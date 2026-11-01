using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.NexusModsLibrary;
using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.Sdk.Library;

// Not SilverModManager.Library.ModSources: App.UI has a static class ModSources and the names would clash.
namespace SilverModManager.Library;

/// <summary>
/// Items downloaded from Nexus Mods.
/// </summary>
internal sealed class NexusModSource : IModSource
{
    public string DisplayName => "Nexus Mods";

    public bool Owns(LibraryItem.ReadOnly item) => NexusModsLibraryItem.TryGet(item.Db, item.Id, out _);

    public Uri? PageUri(LibraryItem.ReadOnly item)
    {
        if (NexusModsLibraryItem.TryGet(item.Db, item.Id, out var nexusItem) is false) return null;
        var modPage = nexusItem.Value.ModPageMetadata;
        return NexusModsUrlBuilder.GetModUri(modPage.GameDomain, modPage.Uid.ModId);
    }

    // The site has no changelog page for a single file (the popup that shows one can't be linked to), only the
    // mod page's own changelog tab, so both actions open the mod page.
    public Uri? ChangelogUri(LibraryItem.ReadOnly item) => PageUri(item);
}
