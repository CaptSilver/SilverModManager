using SilverModManager.Sdk.Library;

namespace SilverModManager.Library.Listing;

/// <summary>
/// A game file fetched from a Steam depot. It lists as <see cref="LibraryItemKinds.Other"/> with Steam as its host and
/// the manifest it came from as its version, since a depot file has no version of its own.
/// </summary>
internal sealed class SteamDepotKind : ILibraryItemKind
{
    public int Order => 420;

    public LibraryItemKindMatch? Match(LibraryItem.ReadOnly item)
    {
        var file = SteamDepotLibraryFile.Load(item.Db, item.Id);
        return file.IsValid() ? new LibraryItemKindMatch(LibraryItemKinds.Other, $"manifest {file.ManifestId}") { Host = "Steam" } : null;
    }
}
