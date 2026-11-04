using SilverModManager.Sdk.Library;

namespace SilverModManager.Library.Listing;

/// <summary>
/// A file downloaded from some host other than Nexus Mods.
/// </summary>
internal sealed class DownloadedFileKind : ILibraryItemKind
{
    public int Order => 200;

    public LibraryItemKindMatch? Match(LibraryItem.ReadOnly item)
        => item.TryGetAsDownloadedFile(out var downloaded)
            ? new LibraryItemKindMatch(LibraryItemKinds.DownloadedFile) { Host = downloaded.DownloadPageUri.Host }
            : null;
}
