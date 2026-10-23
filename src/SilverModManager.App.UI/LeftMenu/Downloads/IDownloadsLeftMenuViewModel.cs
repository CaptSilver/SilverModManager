using System.Collections.ObjectModel;
using SilverModManager.App.UI.LeftMenu.Items;

namespace SilverModManager.App.UI.LeftMenu.Downloads;

public interface IDownloadsLeftMenuViewModel : ILeftMenuViewModel
{
    public ILeftMenuItemViewModel LeftMenuItemAllDownloads { get; }
    
    public ReadOnlyObservableCollection<ILeftMenuItemViewModel> LeftMenuItemsPerGameDownloads { get; }
}