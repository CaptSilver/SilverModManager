using System.Reactive;
using ReactiveUI;
using SilverModManager.App.UI.Controls.Navigation;
using SilverModManager.App.UI.Controls.TopBar;
using SilverModManager.App.UI.LeftMenu;
using SilverModManager.App.UI.LeftMenu.Loadout;
using SilverModManager.App.UI.WorkspaceSystem;

namespace SilverModManager.App.UI.Input.Menu;

public static class AppMenuItems
{
    public const string NoLoadoutReason = "Open a loadout first.";

    /// <summary>
    /// Settings is always there. Apply and Play act on the loadout whose left menu is showing, and carry a reason when
    /// the window is on a page that has none.
    /// </summary>
    public static IReadOnlyList<AppMenuItem> Build(ITopBarViewModel topBar, ILeftMenuViewModel? leftMenu)
    {
        var apply = (leftMenu as ILoadoutLeftMenuViewModel)?.ApplyControlViewModel;
        return
        [
            new AppMenuItem("Settings", () => topBar.OpenSettingsCommand.Execute(NavigationInformation.From(NavigationInput.Default)).Subscribe()),
            apply is null
                ? new AppMenuItem("Apply", static () => { }, NoLoadoutReason)
                : new AppMenuItem("Apply", () => apply.ApplyCommand.Execute(Unit.Default).Subscribe()),
            apply is null
                ? new AppMenuItem("Play", static () => { }, NoLoadoutReason)
                : new AppMenuItem("Play", () => apply.LaunchButtonViewModel.Command.Execute(Unit.Default).Subscribe()),
        ];
    }
}
