using SilverModManager.App.UI.LeftMenu.Items;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.App.UI.LeftMenu.Loadout;

/// <summary>The entries of the left menu's Stores section. A new store is one more line in <see cref="For"/>.</summary>
internal static class StoreLeftMenuItems
{
    /// <summary>The store entries that apply to <paramref name="game"/>, in the order the menu shows them.</summary>
    public static IReadOnlyList<ILeftMenuItemViewModel> For(IGameData game, LoadoutId loadoutId, IWorkspaceController workspaceController, WorkspaceId workspaceId)
    {
        return new[]
            {
                NexusModsLeftMenuItem.For(game, loadoutId, workspaceController, workspaceId),
                ThunderstoreLeftMenuItem.For(game, loadoutId, workspaceController, workspaceId),
            }
            .OfType<ILeftMenuItemViewModel>()
            .ToArray();
    }
}
