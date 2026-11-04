using SilverModManager.Sdk.Library;

namespace SilverModManager.Library.Listing;

/// <summary>
/// A GitHub release asset. It lists as <see cref="LibraryItemKinds.Other"/> with the release tag as its version.
/// </summary>
internal sealed class GitHubReleaseKind : ILibraryItemKind
{
    public int Order => 400;

    public LibraryItemKindMatch? Match(LibraryItem.ReadOnly item)
    {
        var release = GitHubReleaseLibraryFile.Load(item.Db, item.Id);
        return release.IsValid() ? new LibraryItemKindMatch(LibraryItemKinds.Other, release.Tag) : null;
    }
}
