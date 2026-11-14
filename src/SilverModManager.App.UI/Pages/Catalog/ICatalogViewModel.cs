using System.Reactive;
using ReactiveUI;
using SilverModManager.App.UI.Controls.Cards;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.App.UI.Pages.Catalog;

/// <summary>
/// Browses one mod catalog for the loadout's game: a searchable, sortable grid of cards that loads further pages as it
/// is scrolled and installs from its rows. Which source it shows is decided by <see cref="Initialize"/>, so every
/// browsable source gets this page.
/// </summary>
public interface ICatalogViewModel : IPageViewModelInterface
{
    /// <summary>Points the page at a catalog (by its display name) and the loadout it installs into.</summary>
    void Initialize(string catalog, LoadoutId loadoutId);

    /// <summary>The catalog's display name, which is also the page's title.</summary>
    string CatalogName { get; }

    /// <summary>Part of a mod's name; blank lists everything. Changing it searches again after a short pause.</summary>
    string SearchText { get; set; }

    /// <summary>The key of the chosen <see cref="SortOptions"/> entry; remembered per catalog and game.</summary>
    string SortKey { get; set; }

    IReadOnlyList<StoreSortOption> SortOptions { get; }

    /// <summary>The cards found so far.</summary>
    IReadOnlyList<CatalogCardRow> Rows { get; }

    /// <summary>Whether the catalog has more cards than <see cref="Rows"/> shows.</summary>
    bool HasMore { get; }

    bool IsLoading { get; }

    /// <summary>Why the catalog could not be listed; empty when nothing went wrong.</summary>
    string ErrorText { get; }

    /// <summary>What the list is, such as how many matched or that it is a saved copy; empty when there is nothing to add.</summary>
    string StatusText { get; }

    /// <summary>Where the cards get their icons, each fetched when its card is on screen.</summary>
    IThumbnailLoader Icons { get; }

    /// <summary>Appends the next page; the grid asks for it as the user scrolls near the end.</summary>
    ReactiveCommand<Unit, Unit> LoadMoreCommand { get; }

    /// <summary>Installs (or subscribes to) the card's mod. A refusal is shown as a dialog.</summary>
    ReactiveCommand<CatalogCardRow, Unit> InstallCommand { get; }

    /// <summary>Opens the card's page on the source's website.</summary>
    ReactiveCommand<CatalogCardRow, Unit> OpenCommand { get; }
}
