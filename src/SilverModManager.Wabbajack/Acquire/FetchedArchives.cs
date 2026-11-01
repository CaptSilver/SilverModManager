using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Library;
using SilverModManager.Wabbajack.Library;
using SilverModManager.Wabbajack.Models;

namespace SilverModManager.Wabbajack.Acquire;

/// <summary>
/// Records that the library gained a file because a modlist's plan wanted it, by setting
/// <see cref="WabbajackModlistArchive.FetchedFiles"/> of the row for that archive, and adds the list's game to the file.
/// </summary>
/// <remarks>
/// Every list that names the archive gets the mark, not just the one that fetched it. The file has to stay out of the
/// Mods tab while any of those lists still wants it, so removing the first list must not strip the link.
/// </remarks>
internal static class FetchedArchives
{
    /// <summary>
    /// Marks every list's row for <paramref name="archive"/> as fetched; a row already marked is left as it is. A file
    /// that is a member of another archive is never marked.
    /// </summary>
    public static async Task Mark(IConnection connection, FileIdentity archive, LibraryFile.ReadOnly file)
    {
        if (LibraryArchiveLocator.IsArchiveMember(connection.Db, file.Id)) return;
        using var tx = connection.BeginTransaction();
        var any = false;
        foreach (var row in WabbajackModlistArchive.FindByIdentity(connection.Db, archive))
        {
            if (row.FetchedFiles.Any()) continue;
            tx.Add(row.Id, WabbajackModlistArchive.FetchedFiles, file.Id);
            // The file is held for this list now, so it is for the list's game as well as whatever it was already for.
            LibraryDestinations.Add(tx, file.Id, ModlistAcquisition.ForList(row.Modlist.Id, row.Modlist.GameType, null));
            any = true;
        }
        if (any) await tx.Commit();
    }
}
