using SilverModManager.App.UI.Controls.Cards;
using System.Reactive;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.Sdk.Loadouts;
using ReactiveUI;

namespace SilverModManager.App.UI.Pages.Thunderstore;

/// <summary>
/// Browses the Thunderstore community of the loadout's game: the cached package catalog, refreshed when it is
/// stale or on request, filtered by a search box.
/// </summary>
public interface IThunderstoreViewModel : IPageViewModelInterface
{
    LoadoutId LoadoutId { get; set; }

    string SearchText { get; set; }

    /// <summary>Whether deprecated packages are listed; they are hidden by default.</summary>
    bool ShowDeprecated { get; set; }

    /// <summary>The packages matching <see cref="SearchText"/>, best rated first.</summary>
    IReadOnlyList<ThunderstorePackageRow> Packages { get; }

    /// <summary>Where the rows get their package icons, each fetched when its row is on screen.</summary>
    IThumbnailLoader Icons { get; }

    /// <summary>Why the catalog is missing or could not be refreshed; empty when nothing went wrong.</summary>
    string ErrorText { get; }

    bool IsRefreshing { get; }

    /// <summary>Downloads the community's index again, whatever the age of the cached one.</summary>
    ReactiveCommand<Unit, Unit> RefreshCommand { get; }

    /// <summary>Opens a package's page on thunderstore.io in the browser.</summary>
    ReactiveCommand<ThunderstorePackageRow, Unit> OpenPackageCommand { get; }

    /// <summary>
    /// Downloads the package's latest version with the packages it needs and installs them into the loadout, the
    /// mod loader first. A failure ends up in <see cref="ErrorText"/>.
    /// </summary>
    ReactiveCommand<ThunderstorePackageRow, Unit> InstallCommand { get; }
}
