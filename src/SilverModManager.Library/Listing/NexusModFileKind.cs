using SilverModManager.Abstractions.NexusModsLibrary;
using SilverModManager.Sdk.Library;

namespace SilverModManager.Library.Listing;

/// <summary>
/// A file of a Nexus Mods mod page, with the ids of the game, mod and file and the mod page's name and thumbnail.
/// </summary>
internal sealed class NexusModFileKind : ILibraryItemKind
{
    public int Order => 100;

    public LibraryItemKindMatch? Match(LibraryItem.ReadOnly item)
    {
        if (!item.TryGetAsNexusModsLibraryItem(out var nexus)) return null;

        var page = nexus.ModPageMetadata;
        return new LibraryItemKindMatch(LibraryItemKinds.NexusModFile, nexus.FileMetadata.Version)
        {
            NexusGameId = nexus.FileMetadata.Uid.GameId.Value,
            NexusModId = page.Uid.ModId.Value,
            NexusFileId = nexus.FileMetadata.Uid.FileId.Value,
            ModPageId = page.Id.ToString(),
            ModPageName = page.Name,
            ThumbnailUri = NexusModsModPageMetadata.ThumbnailUri.TryGetValue(page, out var uri) ? uri.ToString() : null,
        };
    }
}
