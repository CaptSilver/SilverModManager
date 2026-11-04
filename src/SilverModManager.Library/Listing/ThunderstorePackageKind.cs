using SilverModManager.Sdk.Library;

namespace SilverModManager.Library.Listing;

/// <summary>
/// A Thunderstore package. It lists as <see cref="LibraryItemKinds.Other"/> with the package version as its version.
/// </summary>
internal sealed class ThunderstorePackageKind : ILibraryItemKind
{
    public int Order => 500;

    public LibraryItemKindMatch? Match(LibraryItem.ReadOnly item)
    {
        var package = ThunderstoreLibraryFile.Load(item.Db, item.Id);
        return package.IsValid() ? new LibraryItemKindMatch(LibraryItemKinds.Other, package.VersionNumber) : null;
    }
}
