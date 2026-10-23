using SilverModManager.App.UI.Controls;
using SilverModManager.App.UI.LeftMenu.Items;
using SilverModManager.App.UI.Pages.Thunderstore;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk;
using SilverModManager.UI.Sdk.Icons;

namespace SilverModManager.App.UI.LeftMenu.Loadout;

internal static class ThunderstoreLeftMenuItem
{
    /// <summary>
    /// The page the entry opens, or null for a game whose mods do not come from Thunderstore and so gets no entry.
    /// </summary>
    public static PageData? PageFor(IGameData game, LoadoutId loadoutId)
    {
        if (ThunderstoreGame.For(game) is null) return null;

        return new PageData
        {
            FactoryId = ThunderstorePageFactory.StaticId,
            Context = new ThunderstorePageContext { LoadoutId = loadoutId },
        };
    }

    public static ILeftMenuItemViewModel? For(IGameData game, LoadoutId loadoutId, IWorkspaceController workspaceController, WorkspaceId workspaceId)
    {
        if (PageFor(game, loadoutId) is not { } page) return null;

        return new LeftMenuItemViewModel(workspaceController, workspaceId, page)
        {
            Text = new StringComponent("Thunderstore"),
            Icon = IconValues.Download,
            ToolTip = new StringComponent("Browse and install the mods of this game's Thunderstore community"),
        };
    }
}
