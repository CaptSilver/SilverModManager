using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;

namespace SilverModManager.Abstractions.Library;

/// <summary>
/// One place a library item can come from. <see cref="ModSourceResolver"/> picks it per item. An item no source
/// owns (a local file, a Wabbajack archive, a bundled file) has none, and the UI shows that as disabled menu
/// items. No source invents a page for an item it doesn't know.
/// </summary>
public interface IModSource
{
    /// <summary>
    /// Matches the names in the Source column ("Nexus Mods", "Thunderstore").
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Whether the item was downloaded from this source.
    /// </summary>
    bool Owns(LibraryItem.ReadOnly item);

    /// <summary>
    /// The item's page on the source's site, or null when the source doesn't know the item's page.
    /// </summary>
    Uri? PageUri(LibraryItem.ReadOnly item);

    /// <summary>
    /// Where the source publishes changes for the item. Nexus has no per-file changelog page, so it returns the
    /// mod page.
    /// </summary>
    Uri? ChangelogUri(LibraryItem.ReadOnly item);

    /// <summary>
    /// Brings what the source knows about available updates up to date for one game and tells whoever observes
    /// them. Each source decides whether its own cache is fresh enough: Nexus throttles inside its update
    /// service, Thunderstore asks whether its catalog is stale. <paramref name="force"/> is the Refresh button
    /// and skips that test; entering a page passes false.
    /// </summary>
    Task CheckUpdates(IGameData game, bool force, CancellationToken cancellationToken);
}
