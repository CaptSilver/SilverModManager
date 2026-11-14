using SilverModManager.Library;
using SilverModManager.Library.ModIo;
using SilverModManager.Sdk.Library;

namespace SilverModManager.Library.Listing;

/// <summary>
/// A file downloaded from mod.io. It lists as <see cref="LibraryItemKinds.Other"/> with the modfile's version, or the date
/// the mod was last updated, and mod.io as its host.
/// </summary>
internal sealed class ModIoKind : ILibraryItemKind
{
    public int Order => 411;

    public LibraryItemKindMatch? Match(LibraryItem.ReadOnly item)
    {
        var file = ModIoLibraryFile.Load(item.Db, item.Id);
        return file.IsValid() ? new LibraryItemKindMatch(LibraryItemKinds.Other, ModIoFiles.Held(file)) { Host = "mod.io" } : null;
    }
}
