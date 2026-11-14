using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Library.Catalog;

/// <summary>
/// A mod source the user can browse and install from, as a list of cards. One implementation per source; the catalog
/// page and the <c>catalog</c> verbs work on this and never on a source by name.
/// </summary>
public interface IModCatalog
{
    /// <summary>
    /// What the left menu entry and the verbs call it. Matches <see cref="IModSource.DisplayName"/> of the source it
    /// belongs to.
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Whether the source has mods for the game.
    /// </summary>
    bool Serves(IGameData game);

    /// <summary>
    /// The orders <see cref="Search"/> understands, the first being the default.
    /// </summary>
    IReadOnlyList<CatalogSort> Sorts { get; }

    /// <summary>
    /// One page of cards matching the query, in the query's sort.
    /// </summary>
    Task<CatalogPage> Search(IGameData game, CatalogQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// The card of the mod with this <see cref="CatalogCard.Id"/>, or null when the source has none for the game. The
    /// <c>catalog install</c> verb takes an id, and a search by text does not find one.
    /// </summary>
    Task<CatalogCard?> Find(IGameData game, string id, CancellationToken cancellationToken);

    /// <summary>
    /// Brings the card's mod into the loadout. A deliberate refusal is <see cref="CatalogInstallOutcome.Refused"/>,
    /// not an exception.
    /// </summary>
    Task<CatalogInstallOutcome> Install(CatalogCard card, LoadoutId loadout, CancellationToken cancellationToken);

    /// <summary>
    /// The verb on the card: "Install", or "Subscribe" for a source the game or Steam syncs.
    /// </summary>
    string InstallLabel(IGameData game);
}
