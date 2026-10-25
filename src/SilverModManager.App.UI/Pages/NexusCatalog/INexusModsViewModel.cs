using System.Reactive;
using ReactiveUI;
using SilverModManager.App.UI.Controls.Cards;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.Networking.NexusWebApi;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.App.UI.Pages.NexusCatalog;

/// <summary>
/// Browses the Nexus Mods catalog of the loadout's game: a searchable, sortable grid of cards that loads further pages as
/// it is scrolled and installs from its rows.
/// </summary>
public interface INexusModsViewModel : IPageViewModelInterface
{
    LoadoutId LoadoutId { get; set; }

    /// <summary>Part of a mod's name; blank lists every mod. Changing it searches again after a short pause.</summary>
    string SearchText { get; set; }

    NexusModsSort Sort { get; set; }

    /// <summary>The mods found so far, in the order of <see cref="Sort"/>.</summary>
    IReadOnlyList<NexusModsModRow> Mods { get; }

    /// <summary>Whether the catalog has more mods than <see cref="Mods"/> shows.</summary>
    bool HasMore { get; }

    bool IsLoading { get; }

    /// <summary>Why the catalog or a download failed; empty when nothing went wrong.</summary>
    string ErrorText { get; }

    /// <summary>"Install" for a premium member; "Download on Nexus Mods" for anyone else, whose Install opens the site.</summary>
    string InstallButtonText { get; }

    /// <summary>Supplies the thumbnails of the mod cards.</summary>
    IThumbnailLoader Thumbnails { get; }

    /// <summary>The Collections tab, with its own search and sort.</summary>
    INexusCollectionsViewModel Collections { get; }

    /// <summary>Appends the next page of mods; the page asks for it as the user scrolls near the end of the grid.</summary>
    ReactiveCommand<Unit, Unit> LoadMoreCommand { get; }

    /// <summary>
    /// Premium: downloads the mod's main file into the library. Otherwise, or when the mod has no single main file,
    /// opens the mod's Files tab in the browser.
    /// </summary>
    ReactiveCommand<NexusModsModRow, Unit> InstallCommand { get; }

    /// <summary>Opens the mod's page on nexusmods.com.</summary>
    ReactiveCommand<NexusModsModRow, Unit> OpenCommand { get; }
}
