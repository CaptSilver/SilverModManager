using SilverModManager.App.UI.Controls;
using SilverModManager.App.UI.LeftMenu.Items;
using SilverModManager.App.UI.Resources;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.UI.Sdk;
using SilverModManager.UI.Sdk.Icons;

namespace SilverModManager.App.UI.LeftMenu.Home;

public class HomeLeftMenuDesignViewModel : AViewModel<IHomeLeftMenuViewModel>, IHomeLeftMenuViewModel
{
    public WorkspaceId WorkspaceId { get; } = WorkspaceId.NewId();
    
    public ILeftMenuItemViewModel LeftMenuItemMyGames { get; } = new LeftMenuItemDesignViewModel
    {
        Text = new StringComponent(Language.MyGames),
        Icon = IconValues.GamepadOutline,
    };
    public ILeftMenuItemViewModel LeftMenuItemMyLoadouts { get; } = new LeftMenuItemDesignViewModel
    {
        Text = new StringComponent(Language.MyLoadoutsPageTitle),
        Icon = IconValues.Package,
    };
    
}
