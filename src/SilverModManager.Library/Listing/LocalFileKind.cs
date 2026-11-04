using SilverModManager.Sdk.Library;

namespace SilverModManager.Library.Listing;

/// <summary>
/// A file the user added from disk.
/// </summary>
internal sealed class LocalFileKind : ILibraryItemKind
{
    public int Order => 300;

    public LibraryItemKindMatch? Match(LibraryItem.ReadOnly item)
        => item.TryGetAsLocalFile(out _) ? new LibraryItemKindMatch(LibraryItemKinds.LocalFile) : null;
}
