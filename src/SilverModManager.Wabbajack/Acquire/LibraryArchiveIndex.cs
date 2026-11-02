using SilverModManager.MnemonicDB.Abstractions;
using NexusMods.Paths;
using SilverModManager.Sdk.Library;

namespace SilverModManager.Wabbajack.Acquire;

/// <summary>How a list's path through a library archive is matched to the entries the library recorded for it.</summary>
internal static class LibraryArchiveIndex
{
    /// <summary>The key a path is looked up by: a list's paths differ from the extracted ones in case and separator.</summary>
    public static string Fold(RelativePath path) => path.ToString().Replace('\\', '/').ToLowerInvariant();

    public static Dictionary<string, LibraryArchiveFileEntry.ReadOnly> Build(IDb db, LibraryArchive.ReadOnly archive)
    {
        var index = new Dictionary<string, LibraryArchiveFileEntry.ReadOnly>();
        foreach (var entry in LibraryArchiveFileEntry.FindByParent(db, archive))
        {
            // Two entries that differ only in case collapse to the first, as the install tree will.
            index.TryAdd(Fold(entry.Path), entry);
        }
        return index;
    }
}
