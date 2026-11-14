using SilverModManager.Library;
using SilverModManager.Sdk.Library;

namespace SilverModManager.Library.Listing;

/// <summary>
/// A file downloaded from GameBanana. It lists as <see cref="LibraryItemKinds.Other"/> with the page's version, or the
/// date the page was last updated, and GameBanana as its host.
/// </summary>
internal sealed class GameBananaKind : ILibraryItemKind
{
    public int Order => 410;

    public LibraryItemKindMatch? Match(LibraryItem.ReadOnly item)
    {
        var file = GameBananaLibraryFile.Load(item.Db, item.Id);
        return file.IsValid() ? new LibraryItemKindMatch(LibraryItemKinds.Other, GameBananaVersions.Held(file)) { Host = "GameBanana" } : null;
    }
}
