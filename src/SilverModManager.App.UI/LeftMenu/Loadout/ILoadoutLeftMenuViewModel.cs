using System.Collections.ObjectModel;
using SilverModManager.App.UI.LeftMenu.Items;

namespace SilverModManager.App.UI.LeftMenu.Loadout;

public interface ILoadoutLeftMenuViewModel : ILeftMenuViewModel
{
    public ReadOnlyObservableCollection<ILeftMenuItemViewModel> LeftMenuCollectionItems { get; }
    
    public bool HasSingleCollection { get; }
    
    public IApplyControlViewModel ApplyControlViewModel { get; }
    
    public ILeftMenuItemViewModel LeftMenuItemLibrary { get; }
    
    public ILeftMenuItemViewModel LeftMenuItemLoadout { get; }

    /// <summary>The stores the game's mods can be browsed on, in menu order. Empty for a game on none.</summary>
    public ReadOnlyObservableCollection<ILeftMenuItemViewModel> LeftMenuStoreItems { get; }
    public ILeftMenuItemViewModel LeftMenuItemNewCollection { get; }

    public ILeftMenuItemViewModel LeftMenuItemHealthCheck { get; }
    public ILeftMenuItemViewModel LeftMenuItemTools { get; }
    
    public ILeftMenuItemViewModel? LeftMenuItemExternalChanges { get; }
}
