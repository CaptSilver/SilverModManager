using NexusMods.Hashing.xxHash3;
using NexusMods.Sdk.Library;
using NexusMods.Wabbajack.Format;

namespace NexusMods.Wabbajack.Acquire;

/// <summary>Where an archive of a modlist stands against the library and the game folder.</summary>
public enum ArchiveState
{
    /// <summary>The library holds it.</summary>
    Have,

    /// <summary>The app can download it without the user: plain HTTP, the Wabbajack CDN, or Nexus Mods with Premium.</summary>
    Automatic,

    /// <summary>A Nexus Mods file the user fetches with "Mod manager download" on its page.</summary>
    Browser,

    /// <summary>The user has to get it from <see cref="ArchivePlanRow.Page"/> and drop it in.</summary>
    Manual,

    /// <summary>A file of the game itself, and this install has the version the list wants.</summary>
    GameFile,

    /// <summary>A file of the game itself, but this install's copy differs from the list's or is missing.</summary>
    GameFileVersion,

    /// <summary>A source the app cannot fetch from.</summary>
    Unsupported,
}

/// <summary>
/// One archive of the list and what to do about it. <paramref name="Library"/> is set for <see cref="ArchiveState.Have"/>;
/// <paramref name="Page"/> for the states that send the user somewhere. <paramref name="Opened"/> is set once a
/// <see cref="ArchiveState.Browser"/> row's page has been opened in the user's browser.
/// </summary>
public sealed record ArchivePlanRow(ArchiveEntry Archive, ArchiveState State, string? Reason, Uri? Page, LibraryFile.ReadOnly? Library, bool Opened = false);

/// <summary>The rows for every archive of a list, in the list's order.</summary>
public sealed record ArchivePlan(IReadOnlyList<ArchivePlanRow> Rows)
{
    /// <summary>The archive entry the list names with that xxHash64, if any.</summary>
    public ArchiveEntry? FindArchive(Hash xxHash64)
    {
        foreach (var row in Rows)
        {
            if (row.Archive.XxHash64 == xxHash64) return row.Archive;
        }
        return null;
    }
}
