using System.Collections.ObjectModel;
using SilverModManager.App.UI.LeftMenu.Items;

namespace SilverModManager.App.UI.LeftMenu.Loadout;

public interface ILoadoutLeftMenuViewModel : ILeftMenuViewModel
{
    public ReadOnlyObservableCollection<ILeftMenuItemViewModel> LeftMenuCollectionItems { get; }
    
    /// <summary>
    /// Moves the collection at <paramref name="fromIndex"/> of <see cref="LeftMenuCollectionItems"/> to
    /// <paramref name="toIndex"/> and saves the order. Both must be <see cref="IsCollectionMovable"/>.
    /// </summary>
    public void MoveCollection(int fromIndex, int toIndex);

    /// <summary>Whether the entry at <paramref name="index"/> of <see cref="LeftMenuCollectionItems"/> is a collection that can be dragged.</summary>
    public bool IsCollectionMovable(int index);

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
