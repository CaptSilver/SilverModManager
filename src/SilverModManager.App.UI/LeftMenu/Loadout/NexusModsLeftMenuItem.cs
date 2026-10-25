using SilverModManager.App.UI.Controls;
using SilverModManager.App.UI.Helpers;
using SilverModManager.App.UI.LeftMenu.Items;
using SilverModManager.App.UI.Pages.NexusCatalog;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk;
using SilverModManager.UI.Sdk.Icons;

namespace SilverModManager.App.UI.LeftMenu.Loadout;

internal static class NexusModsLeftMenuItem
{
    /// <summary>The page the entry opens, or null for a game that is not on Nexus Mods and so gets no entry.</summary>
    public static PageData? PageFor(IGameData game, LoadoutId loadoutId)
    {
        if (!game.IsOnNexusMods()) return null;

        return new PageData
        {
            FactoryId = NexusModsPageFactory.StaticId,
            Context = new NexusModsPageContext { LoadoutId = loadoutId },
        };
    }

    public static ILeftMenuItemViewModel? For(IGameData game, LoadoutId loadoutId, IWorkspaceController workspaceController, WorkspaceId workspaceId)
    {
        if (PageFor(game, loadoutId) is not { } page) return null;

        return new LeftMenuItemViewModel(workspaceController, workspaceId, page)
        {
            Text = new StringComponent("Nexus Mods"),
            Icon = IconValues.Download,
            ToolTip = new StringComponent("Browse and install the mods of this game on Nexus Mods"),
        };
    }
}
