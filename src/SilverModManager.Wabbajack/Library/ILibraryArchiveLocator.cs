using NexusMods.Hashing.xxHash3;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk.Library;

namespace NexusMods.Wabbajack.Library;

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
    LibraryFile.ReadOnly? FindByXxHash64(IDb db, Hash hash);
}

public sealed class LibraryArchiveLocator : ILibraryArchiveLocator
{
    public LibraryFile.ReadOnly? FindByXxHash64(IDb db, Hash hash)
    {
        foreach (var datom in db.Datoms(LibraryFile.XxHash64, hash))
        {
            var file = LibraryFile.Load(db, datom.E);
            if (file.IsValid()) return file;
        }
        return null;
    }
}
