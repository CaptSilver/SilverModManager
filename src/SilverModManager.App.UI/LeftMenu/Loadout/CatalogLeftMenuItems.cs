using SilverModManager.Abstractions.Library.Catalog;
using SilverModManager.App.UI.Controls;
using SilverModManager.App.UI.LeftMenu.Items;
using SilverModManager.App.UI.Pages.Catalog;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk;
using SilverModManager.UI.Sdk.Icons;

namespace SilverModManager.App.UI.LeftMenu.Loadout;

/// <summary>
/// One left menu entry per catalog that serves the game, each opening the generic catalog page.
/// </summary>
internal static class CatalogLeftMenuItems
{
    /// <summary>
    /// The catalogs the generic page lists for <paramref name="game"/>. Nexus Mods is left out: it keeps its own page,
    /// which also lists collections, and its own entry.
    /// </summary>
    public static IEnumerable<IModCatalog> Browsable(ModCatalogs catalogs, IGameData game)
        => catalogs.For(game).Where(catalog => catalog.DisplayName != NexusCatalogName);

    internal const string NexusCatalogName = "Nexus Mods";

    public static PageData PageFor(IModCatalog catalog, LoadoutId loadoutId) => new()
    {
        FactoryId = CatalogPageFactory.StaticId,
        Context = new CatalogPageContext { LoadoutId = loadoutId, Catalog = catalog.DisplayName },
    };

    public static IEnumerable<ILeftMenuItemViewModel> For(ModCatalogs catalogs, IGameData game, LoadoutId loadoutId, IWorkspaceController workspaceController, WorkspaceId workspaceId)
        => Browsable(catalogs, game).Select(catalog => (ILeftMenuItemViewModel)new LeftMenuItemViewModel(workspaceController, workspaceId, PageFor(catalog, loadoutId))
        {
            Text = new StringComponent(catalog.DisplayName),
            Icon = IconValues.Download,
            ToolTip = new StringComponent($"Browse and install the mods of this game from {catalog.DisplayName}"),
        });
}
