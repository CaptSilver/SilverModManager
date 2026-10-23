using SilverModManager.App.UI.LeftMenu.Items;

namespace SilverModManager.App.UI.LeftMenu.Home;

public interface IHomeLeftMenuViewModel : ILeftMenuViewModel
{
    public ILeftMenuItemViewModel LeftMenuItemMyGames { get; }
    
    public ILeftMenuItemViewModel LeftMenuItemMyLoadouts { get; }
    
}
