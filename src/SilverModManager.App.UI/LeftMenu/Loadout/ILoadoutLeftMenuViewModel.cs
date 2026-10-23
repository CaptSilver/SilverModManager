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

    /// <summary>Null for a game whose mods do not come from Thunderstore.</summary>
    public ILeftMenuItemViewModel? LeftMenuItemThunderstore { get; }
    public ILeftMenuItemViewModel LeftMenuItemNewCollection { get; }

    public ILeftMenuItemViewModel LeftMenuItemHealthCheck { get; }
    public ILeftMenuItemViewModel LeftMenuItemTools { get; }
    
    public ILeftMenuItemViewModel? LeftMenuItemExternalChanges { get; }
}
