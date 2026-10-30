using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Library;
using SilverModManager.Wabbajack.Models;

namespace SilverModManager.Wabbajack.Acquire;

/// <summary>
/// Records that the library gained a file because a modlist's plan wanted it, by setting
/// <see cref="WabbajackModlistArchive.FetchedFiles"/> of the list's row for that archive.
/// </summary>
internal static class FetchedArchives
{
    /// <summary>Marks the list's row for <paramref name="archive"/> as fetched; a row already marked is left as it is.</summary>
    public static async Task Mark(IConnection connection, LibraryFileId modlist, FileIdentity archive, LibraryFile.ReadOnly file)
    {
        using var tx = connection.BeginTransaction();
        var any = false;
        foreach (var row in WabbajackModlistArchive.FindByModlist(connection.Db, modlist.Value))
        {
            if (!row.Identity.Equals(archive) || row.FetchedFiles.Any()) continue;
            tx.Add(row.Id, WabbajackModlistArchive.FetchedFiles, file.Id);
            any = true;
        }
        if (any) await tx.Commit();
    }
}
