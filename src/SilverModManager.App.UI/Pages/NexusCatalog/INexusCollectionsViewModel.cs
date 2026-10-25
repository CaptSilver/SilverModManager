using System.ComponentModel;
using System.Reactive;
using ReactiveUI;
using SilverModManager.App.UI.Controls.Cards;
using SilverModManager.Networking.NexusWebApi;

namespace SilverModManager.App.UI.Pages.NexusCatalog;

/// <summary>
/// The Collections tab of the Nexus Mods page: a searchable, sortable grid of the loadout's game's collections. It keeps
/// its own search and sort, separate from the Mods tab's.
/// </summary>
public interface INexusCollectionsViewModel : INotifyPropertyChanged
{
    /// <summary>Part of a collection's name; blank lists every collection. Changing it searches again after a short pause.</summary>
    string SearchText { get; set; }

    NexusCollectionsSort Sort { get; set; }

    /// <summary>The collections found so far, in the order of <see cref="Sort"/>.</summary>
    IReadOnlyList<NexusCollectionRow> Collections { get; }

    bool HasMore { get; }

    bool IsLoading { get; }

    /// <summary>Why the catalog or an install failed; empty when nothing went wrong.</summary>
    string ErrorText { get; }

    /// <summary>Whether the tab is on screen. Nothing is searched until it first is.</summary>
    bool IsVisible { get; set; }

    /// <summary>Supplies the images of the collection cards.</summary>
    IThumbnailLoader Thumbnails { get; }

    /// <summary>Appends the next page of collections; the page asks for it as the user scrolls near the end of the grid.</summary>
    ReactiveCommand<Unit, Unit> LoadMoreCommand { get; }

    /// <summary>
    /// Install: adds the collection's latest revision the way an nxm:// collection link does, which opens its download
    /// page. Open, for a collection the library already holds: opens that page.
    /// </summary>
    ReactiveCommand<NexusCollectionRow, Unit> PrimaryCommand { get; }

    /// <summary>Opens the collection's page on nexusmods.com.</summary>
    ReactiveCommand<NexusCollectionRow, Unit> OpenOnNexusCommand { get; }
}
