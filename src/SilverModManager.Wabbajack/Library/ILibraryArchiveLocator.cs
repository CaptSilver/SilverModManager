using SilverModManager.Sdk.Hashes;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Library;

namespace SilverModManager.Wabbajack.Library;

/// <summary>
/// Finds library files by the xxHash64 a modlist names them with.
/// </summary>
public interface ILibraryArchiveLocator
{
    /// <summary>
    /// Any library file with that xxHash64, archive or not (a modlist's download may be a plain file such as a
    /// <c>.esp</c>). Callers that need an archive use <c>TryGetAsLibraryArchive</c> on the result. Files added
    /// before the hash was recorded are not found.
    /// </summary>
    LibraryFile.ReadOnly? FindByXxHash64(IDb db, FileIdentity identity);

    /// <summary>
    /// Like <see cref="FindByXxHash64"/> but skips files that are members of another library archive. Recording a
    /// file as fetched for a modlist needs this: a member belongs to the archive that holds it, and removing it
    /// would cut it out of that archive.
    /// </summary>
    LibraryFile.ReadOnly? FindTopLevelByXxHash64(IDb db, FileIdentity identity);
}

public sealed class LibraryArchiveLocator : ILibraryArchiveLocator
{
    public LibraryFile.ReadOnly? FindByXxHash64(IDb db, FileIdentity identity) => Find(db, identity, topLevelOnly: false);

    public LibraryFile.ReadOnly? FindTopLevelByXxHash64(IDb db, FileIdentity identity) => Find(db, identity, topLevelOnly: true);

    /// <summary>Whether the file is a member extracted from another library archive rather than a file the library holds itself.</summary>
    public static bool IsArchiveMember(IDb db, EntityId file) => LibraryArchiveFileEntry.Load(db, file).IsValid();

    private static LibraryFile.ReadOnly? Find(IDb db, FileIdentity identity, bool topLevelOnly)
    {
        if (!identity.TryXxHash64(out var hash)) return null;
        foreach (var datom in db.Datoms(LibraryFile.XxHash64, hash))
        {
            var file = LibraryFile.Load(db, datom.E);
            if (!file.IsValid()) continue;
            if (topLevelOnly && IsArchiveMember(db, file.Id)) continue;
            return file;
        }
        return null;
    }
}
