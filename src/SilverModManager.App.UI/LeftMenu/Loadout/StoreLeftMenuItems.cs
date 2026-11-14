using SilverModManager.Abstractions.Library.Catalog;
using SilverModManager.App.UI.LeftMenu.Items;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.App.UI.LeftMenu.Loadout;

/// <summary>The entries of the left menu's Stores section. Nexus Mods and the Wabbajack gallery have pages of their own; every other source is a registered catalog.</summary>
internal static class StoreLeftMenuItems
{
    /// <summary>The store entries that apply to <paramref name="game"/>, in the order the menu shows them.</summary>
    public static IReadOnlyList<ILeftMenuItemViewModel> For(ModCatalogs catalogs, IGameData game, LoadoutId loadoutId, IWorkspaceController workspaceController, WorkspaceId workspaceId)
    {
        return new[] { NexusModsLeftMenuItem.For(game, loadoutId, workspaceController, workspaceId) }
            .OfType<ILeftMenuItemViewModel>()
            .Concat(CatalogLeftMenuItems.For(catalogs, game, loadoutId, workspaceController, workspaceId))
            .Concat(new[] { WabbajackGalleryLeftMenuItem.For(game, loadoutId, workspaceController, workspaceId) }.OfType<ILeftMenuItemViewModel>())
            .ToArray();
    }
}
