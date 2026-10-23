using NexusMods.Sdk.FileStore;
using NexusMods.Sdk.Library;

namespace NexusMods.Wabbajack.Format;

/// <summary>
/// Reads the members of a <c>.wabbajack</c> file after it was added to the library. The file is a zip, so adding it
/// extracts every member into the store and the modlist is read back from there; there is no second reader.
/// </summary>
public static class ModlistArchive
{
    private static readonly string[] ModlistEntryNames = ["modlist", "modlist.json"];

    public static async Task<ModList> ReadModlist(IFileStore store, LibraryArchive.ReadOnly archive, CancellationToken cancellationToken = default)
    {
        var entry = archive.Children.FirstOrDefault(c => ModlistEntryNames.Contains(c.Path.ToString(), StringComparer.OrdinalIgnoreCase));
        if (!entry.IsValid())
            throw new UnsupportedModlistException("The file has no 'modlist' entry, so it is not a Wabbajack modlist");

        await using var stream = await store.GetFileStream(entry.AsLibraryFile().Hash, cancellationToken);
        return ModListReader.Read(stream);
    }

    /// <summary>Opens the member a directive's <c>SourceDataID</c> or <c>PatchID</c> names.</summary>
    public static async Task<Stream> ReadBlob(IFileStore store, LibraryArchive.ReadOnly archive, string id, CancellationToken cancellationToken = default)
    {
        var entry = archive.Children.FirstOrDefault(c => string.Equals(c.Path.ToString(), id, StringComparison.OrdinalIgnoreCase));
        if (!entry.IsValid())
            throw new UnsupportedModlistException($"The modlist refers to '{id}', which is not in the file");

        return await store.GetFileStream(entry.AsLibraryFile().Hash, cancellationToken);
    }
}
