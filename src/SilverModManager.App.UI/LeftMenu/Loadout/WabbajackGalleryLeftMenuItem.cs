using SilverModManager.App.UI.Controls;
using SilverModManager.App.UI.LeftMenu.Items;
using SilverModManager.App.UI.Pages.WabbajackGallery;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk;
using SilverModManager.UI.Sdk.Icons;
using SilverModManager.Wabbajack.Gallery;

namespace SilverModManager.App.UI.LeftMenu.Loadout;

internal static class WabbajackGalleryLeftMenuItem
{
    /// <summary>The page the entry opens, or null for a game the Wabbajack gallery has no lists for and so gets no entry.</summary>
    public static PageData? PageFor(IGameData game, LoadoutId loadoutId)
    {
        if (!GalleryGames.HasLists(game.GameId)) return null;

        return new PageData
        {
            FactoryId = WabbajackGalleryPageFactory.StaticId,
            Context = new WabbajackGalleryPageContext { LoadoutId = loadoutId },
        };
    }

    public static ILeftMenuItemViewModel? For(IGameData game, LoadoutId loadoutId, IWorkspaceController workspaceController, WorkspaceId workspaceId)
    {
        if (PageFor(game, loadoutId) is not { } page) return null;

        return new LeftMenuItemViewModel(workspaceController, workspaceId, page)
        {
            Text = new StringComponent("Wabbajack"),
            Icon = IconValues.Download,
            ToolTip = new StringComponent("Browse Wabbajack's public modlists for this game and install one"),
        };
    }
}
