using SilverModManager.Library.Listing;
using SilverModManager.Sdk.Library;
using SilverModManager.Wabbajack.Models;

namespace SilverModManager.Wabbajack.Library;

/// <summary>
/// Tells <c>library list</c> which library items are Wabbajack modlists; the library cannot see that model itself.
/// </summary>
public sealed class WabbajackModlistKind : ILibraryItemKind
{
    public LibraryItemKindMatch? Match(LibraryItem.ReadOnly item)
    {
        var modlist = WabbajackModlistLibraryFile.Load(item.Db, item.Id);
        return modlist.IsValid() ? new LibraryItemKindMatch(LibraryItemKinds.WabbajackModlist, modlist.Version) : null;
    }
}
