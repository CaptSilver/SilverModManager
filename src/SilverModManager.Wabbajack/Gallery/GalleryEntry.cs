using NexusMods.Hashing.xxHash3;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Wabbajack.Gallery;

/// <summary>
/// One modlist of Wabbajack's public gallery: what a card shows, and what the download needs to check the file.
/// Every field comes from untrusted JSON, so anything missing or malformed is absent here rather than an error.
/// </summary>
public sealed record GalleryEntry
{
    public required string Title { get; init; }
    public string Description { get; init; } = "";
    public required string Author { get; init; }
    public IReadOnlyList<string> Maintainers { get; init; } = [];
    public IReadOnlyList<string> Tags { get; init; } = [];
    public string Version { get; init; } = "";

    /// <summary>The gallery's id for the game, e.g. <c>skyrimspecialedition</c>.</summary>
    public required string GalleryGame { get; init; }

    /// <summary>The game of this app the list is for, or null when the app has no title for it.</summary>
    public GameId? Game => GalleryGames.GameIdFor(GalleryGame);

    /// <summary>The list's id within the gallery; unique among the gallery's lists.</summary>
    public required string MachineUrl { get; init; }

    /// <summary>The name of the repository the entry came from, e.g. <c>wabbajack-tools/mod-lists</c>.</summary>
    public required string Repository { get; init; }

    public bool IsOfficial { get; init; }
    public bool IsNsfw { get; init; }
    public bool IsUtility { get; init; }

    /// <summary>The author or the gallery has switched the list off; it must not be offered.</summary>
    public bool IsForcedDown { get; init; }

    public Uri? Image { get; init; }
    public Uri? Readme { get; init; }
    public Uri? Download { get; init; }

    /// <summary>The author's own page for the list, when they gave one.</summary>
    public Uri? Website { get; init; }

    public DateTimeOffset? DateCreated { get; init; }
    public DateTimeOffset? DateUpdated { get; init; }

    /// <summary>The xxHash64 of the <c>.wabbajack</c> file.</summary>
    public Hash? DownloadHash { get; init; }

    /// <summary>The size of the <c>.wabbajack</c> file in bytes.</summary>
    public ulong? DownloadSize { get; init; }

    public int? ArchiveCount { get; init; }
    public ulong? ArchivesSize { get; init; }
    public int? InstalledFileCount { get; init; }
    public ulong? InstalledSize { get; init; }

    /// <summary>The entry names a file and the hash and size to check it against, so it can be fetched safely.</summary>
    public bool IsDownloadable => Download is not null && DownloadHash is not null && DownloadSize is not null;
}

public static class GalleryEntries
{
    /// <summary>
    /// The entries a page may show. <paramref name="showAdult"/> is the app-wide Show adult content setting; lists
    /// that are forced down are hidden whatever it says.
    /// </summary>
    public static IEnumerable<GalleryEntry> Visible(this IEnumerable<GalleryEntry> entries, bool showAdult)
        => entries.Where(e => !e.IsForcedDown && (showAdult || !e.IsNsfw));

    public static IEnumerable<GalleryEntry> ForGame(this IEnumerable<GalleryEntry> entries, GameId game)
        => entries.Where(e => e.Game == game);
}
