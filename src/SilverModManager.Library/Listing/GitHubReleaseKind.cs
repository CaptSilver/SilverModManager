using SilverModManager.Sdk.Library;

namespace SilverModManager.Library.Listing;

/// <summary>
/// A GitHub release asset. It lists as <see cref="LibraryItemKinds.Other"/> with the release tag as its version and
/// GitHub as its host.
/// </summary>
internal sealed class GitHubReleaseKind : ILibraryItemKind
{
    internal const string TrustOnFirstUseNote = "pinned on first download, not verified by GitHub";

    public int Order => 400;

    public LibraryItemKindMatch? Match(LibraryItem.ReadOnly item)
    {
        var release = GitHubReleaseLibraryFile.Load(item.Db, item.Id);
        if (!release.IsValid()) return null;

        // Only a row that says its pin was taken from the first download gets the note: one added before that was
        // recorded may well have had a digest from GitHub, and claiming otherwise would be a guess.
        var firstUse = GitHubReleaseLibraryFile.DigestAdvertised.TryGetValue(release, out var advertised) && !advertised;
        return new LibraryItemKindMatch(LibraryItemKinds.Other, release.Tag) { Host = "GitHub", Note = firstUse ? TrustOnFirstUseNote : null };
    }
}
