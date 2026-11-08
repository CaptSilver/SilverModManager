using Avalonia.Media.Imaging;
using SilverModManager.App.UI.Controls.Navigation;
using SilverModManager.App.UI.Pages.CollectionDownload;
using SilverModManager.App.UI.Pages.Collections;
using SilverModManager.App.UI.Pages.Collections.Update;
using SilverModManager.App.UI.WorkspaceSystem;

namespace SilverModManager.App.UI.Pages.LoadoutPage;

/// <summary>
/// What the collection page shows, whatever kind of collection it is. The values come from the page's
/// <see cref="CollectionSource"/>; the view binds to this alone.
/// </summary>
public interface ICollectionLoadoutViewModel : IPageViewModelInterface
{
    /// <summary>
    /// The load order tree of the installed group. Null while there is no group, and then there is no Installed tab.
    /// </summary>
    LoadoutTreeDataGridAdapter? Adapter { get; }

    /// <summary>Whether the Installed tab is offered: a group exists, even a half-written one.</summary>
    bool HasInstalledTab { get; }

    /// <summary>Whether the header's enable toggle is shown.</summary>
    bool IsToggleVisible { get; }

    /// <summary>
    /// Gets whether the collection is read-only.
    /// </summary>
    bool IsReadOnly { get; }

    /// <summary>
    /// Gets whether the collection is enabled.
    /// </summary>
    bool IsCollectionEnabled { get; }

    /// <summary>
    /// Gets the number of mods installed in the collection, both required and optional.
    /// </summary>
    int InstalledModsCount { get; }

    string Name { get; }

    /// <summary>The newer version of the installed collection, or null when none is known. The header pill shows it.</summary>
    ICollectionUpdate? Update { get; }

    /// <summary>The header's first line: whether the collection is installed.</summary>
    string StateLine { get; }

    /// <summary>"Revision 193", "Version 3.2.1" or empty.</summary>
    string VersionText { get; }

    /// <summary>The collapsible description block under the header; empty hides it.</summary>
    string Description { get; }

    /// <summary>The header's numbers. A null field hides its panel.</summary>
    CollectionStats Stats { get; }

    string AuthorName { get; }

    Bitmap? AuthorAvatar { get; }

    Bitmap? TileImage { get; }

    Bitmap? BackgroundImage { get; }

    /// <summary>Where the collection comes from, shown in the toolbar. Null for a local collection.</summary>
    CollectionBadge? Badge { get; }

    /// <summary>The toolbar's "Open on Nexus Mods" or "Open readme" button, when there is one.</summary>
    CollectionLink? Link { get; }

    /// <summary>The "more" menu: the source's items, then the removal item while the collection is installed.</summary>
    IReadOnlyList<IContextMenuItem> MenuItems { get; }

    R3.ReactiveCommand<R3.Unit> CommandToggle { get; }

    /// <summary>Emits when the service refused a toggle: nothing changed, so the switch has to go back to the state it shows.</summary>
    R3.Observable<R3.Unit> ToggleRefused { get; }

    /// <summary>
    /// The tab being shown.
    /// </summary>
    CollectionPageTab SelectedTab { get; set; }

    /// <summary>
    /// The collection's download page, shown as the Downloads tab. Null when there is nothing to download from.
    /// </summary>
    IPageViewModelInterface? Downloads { get; }

    /// <summary>
    /// The collection author's instructions. Empty when there are none or they could not be read.
    /// </summary>
    CollectionInstructions Instructions { get; }

    /// <summary>
    /// The install running into this collection, shown in the header whichever tab is open. Null while none is.
    /// </summary>
    CollectionInstallStatus? InstallProgress { get; }
}
