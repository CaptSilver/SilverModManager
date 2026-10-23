using System.Collections.ObjectModel;
using JetBrains.Annotations;
using SilverModManager.App.UI.Controls;
using SilverModManager.App.UI.Controls.Navigation;
using SilverModManager.App.UI.LeftMenu.Items;
using SilverModManager.App.UI.Pages.MyGames;
using SilverModManager.App.UI.Pages.MyLoadouts;
using SilverModManager.App.UI.Resources;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.UI.Sdk;
using SilverModManager.UI.Sdk.Icons;
using ReactiveUI;

namespace SilverModManager.App.UI.LeftMenu.Home;

[UsedImplicitly]
public class HomeLeftMenuViewModel : AViewModel<IHomeLeftMenuViewModel>, IHomeLeftMenuViewModel
{
    public WorkspaceId WorkspaceId { get; }
    public ILeftMenuItemViewModel LeftMenuItemMyGames { get; }
    public ILeftMenuItemViewModel LeftMenuItemMyLoadouts { get; }

    public HomeLeftMenuViewModel(
        IMyGamesViewModel myGamesViewModel,
        WorkspaceId workspaceId,
        IWorkspaceController workspaceController)
    {
        WorkspaceId = workspaceId;

        LeftMenuItemMyGames = new LeftMenuItemViewModel(
            workspaceController,
            WorkspaceId,
            new PageData
            {
                FactoryId = MyGamesPageFactory.StaticId,
                Context = new MyGamesPageContext(),
            }
        )
        {
            Text = new StringComponent(Language.MyGames),
            Icon = IconValues.GamepadOutline,
        };
        
        LeftMenuItemMyLoadouts = new LeftMenuItemViewModel(
            workspaceController,
            WorkspaceId,
            new PageData
            {
                FactoryId = MyLoadoutsPageFactory.StaticId,
                Context = new MyLoadoutsPageContext(),
            }
        )
        {
            Text = new StringComponent(Language.MyLoadoutsPageTitle),
            Icon = IconValues.Package,
        };
    }
}
