using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Library;
using SilverModManager.Wabbajack.Format;
using SilverModManager.Wabbajack.Library;

namespace SilverModManager.Wabbajack.Acquire;

/// <summary>Where an archive of a modlist stands against the library and the game folder.</summary>
public enum ArchiveState
{
    /// <summary>The library holds it.</summary>
    Have,

    /// <summary>The app can download it without the user: plain HTTP, the Wabbajack CDN, or Nexus Mods with Premium.</summary>
    Automatic,

    /// <summary>A Nexus Mods file the user fetches with "Mod manager download" on its page.</summary>
    Browser,

    /// <summary>A forum file the app could fetch once the user signs in to the site; <see cref="ArchivePlanRow.Page"/> is the file's page.</summary>
    SignIn,

    /// <summary>The user has to get it from <see cref="ArchivePlanRow.Page"/> and drop it in.</summary>
    Manual,

    /// <summary>A file of the game itself, and this install has the version the list wants.</summary>
    GameFile,

    /// <summary>A file of the game itself, but this install's copy differs from the list's or is missing.</summary>
    GameFileVersion,

    /// <summary>A source the app cannot fetch from.</summary>
    Unsupported,

    /// <summary>Mod Organizer 2 itself, which the list ships and the app replaces. It is never fetched and never blocks an install.</summary>
    NotNeeded,
}

/// <summary>
/// One archive of the list and what to do about it. <paramref name="Library"/> is set for <see cref="ArchiveState.Have"/>
/// and for a row that holds a copy lacking bytes the list needs (see <see cref="HoldsIncompleteCopy"/>);
/// <paramref name="Page"/> for the states that send the user somewhere. <paramref name="Opened"/> is set once a
/// <see cref="ArchiveState.Browser"/> row's page has been opened in the user's browser. <paramref name="Note"/> explains
/// a row that is not what its name suggests, such as a Mod Organizer 2 release the list still takes a file from.
/// <paramref name="DownloadFailed"/> is set when the app tried to download the archive and could not; the row then waits
/// for the user like a Manual one, but a retry can still fetch it.
/// </summary>
public sealed record ArchivePlanRow(ArchiveEntry Archive, ArchiveState State, string? Reason, Uri? Page, LibraryFile.ReadOnly? Library, bool Opened = false, string? Note = null, bool DownloadFailed = false)
{
    /// <summary>
    /// The library holds the archive but without the bytes of nested archives the list copies whole, so the row still
    /// waits for a fresh copy. Everything else the list reads from the held copy can be installed.
    /// </summary>
    public bool HoldsIncompleteCopy => State != ArchiveState.Have && Library is not null;

    /// <summary>
    /// A retry can fetch it: its download failed, or it is an automatic row that still carries the reason of an
    /// earlier failure. A Manual row the list gave without a host the app can read is never tried.
    /// </summary>
    public bool CanRetry => DownloadFailed || (State == ArchiveState.Automatic && !string.IsNullOrEmpty(Reason));
}

/// <summary>
/// The rows for every archive of a list, in the list's order. <paramref name="Modlist"/> is the library file of the
/// list the plan was built for; it is how a download is credited to the list that wanted it. <paramref name="Check"/>
/// says which held archives lack something the list copies out of them (see <see cref="HeldArchiveCheck"/>).
/// </summary>
public sealed record ArchivePlan(IReadOnlyList<ArchivePlanRow> Rows, LibraryFileId? Modlist = null, HeldArchiveCheck? Check = null)
{
    /// <summary>Whether the library's <paramref name="held"/> copy of the list's <paramref name="archive"/> has all the list needs from it.</summary>
    public bool IsComplete(IDb db, FileIdentity archive, LibraryFile.ReadOnly held)
        => (Check ?? HeldArchiveCheck.NothingNeeded).IsComplete(db, archive, held);

    /// <summary>Whether the library holds the list's <paramref name="archive"/> with all the list needs from it (see <see cref="HeldArchiveCheck.IsHeld"/>).</summary>
    public bool IsHeld(IDb db, ILibraryArchiveLocator locator, FileIdentity archive)
        => (Check ?? HeldArchiveCheck.NothingNeeded).IsHeld(db, locator, archive);

    /// <summary>The archive entry the list names with that xxHash64, if any.</summary>
    public ArchiveEntry? FindArchive(FileIdentity xxHash64)
    {
        foreach (var row in Rows)
        {
            if (row.Archive.XxHash64.Equals(xxHash64)) return row.Archive;
        }
        return null;
    }
}
