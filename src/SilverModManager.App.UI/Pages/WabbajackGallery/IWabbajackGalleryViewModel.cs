using System.Reactive;
using ReactiveUI;
using SilverModManager.App.UI.Controls.Cards;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.App.UI.Pages.WabbajackGallery;

/// <summary>
/// Browses Wabbajack's public modlist gallery for the loadout's game: the cached gallery, refreshed when it is stale
/// or on request, filtered by a search box, with a button per list to install it.
/// </summary>
public interface IWabbajackGalleryViewModel : IPageViewModelInterface
{
    LoadoutId LoadoutId { get; set; }

    /// <summary>Words that each have to appear in a list's title, author or tags.</summary>
    string SearchText { get; set; }

    /// <summary>How <see cref="Lists"/> is ordered; remembered per game.</summary>
    WabbajackGallerySort Sort { get; set; }

    /// <summary>The lists matching <see cref="SearchText"/>, in the order of <see cref="Sort"/>.</summary>
    IReadOnlyList<WabbajackGalleryRow> Lists { get; }

    /// <summary>Where the cards get their pictures, each fetched when its card is on screen.</summary>
    IThumbnailLoader Thumbnails { get; }

    /// <summary>Why the gallery is missing or a download failed; empty when nothing went wrong.</summary>
    string ErrorText { get; }

    /// <summary>"Last refreshed 1 Oct 12:00", with a note when that is an offline copy; empty before the first load.</summary>
    string RefreshedText { get; }

    bool IsRefreshing { get; }

    /// <summary>Reads the gallery again, whatever the age of the cached copy.</summary>
    ReactiveCommand<Unit, Unit> RefreshCommand { get; }

    /// <summary>
    /// Downloads the list into the library and opens its modlist page, which runs the install. A list already in the
    /// library is opened without a download. A failure ends up in <see cref="ErrorText"/>.
    /// </summary>
    ReactiveCommand<WabbajackGalleryRow, Unit> InstallCommand { get; }

    /// <summary>Opens a list's readme or website in the browser.</summary>
    ReactiveCommand<Uri, Unit> OpenLinkCommand { get; }
}
