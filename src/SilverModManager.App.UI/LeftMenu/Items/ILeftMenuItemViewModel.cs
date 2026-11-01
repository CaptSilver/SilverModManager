using System.Reactive;
using SilverModManager.App.UI.Controls;
using SilverModManager.App.UI.Controls.DragAndDrop;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.Controls.Navigation;
using SilverModManager.UI.Sdk;
using SilverModManager.UI.Sdk.Icons;
using ReactiveUI;

namespace SilverModManager.App.UI.LeftMenu.Items;

public interface ILeftMenuItemViewModel : IViewModelInterface
{
    public StringComponent Text { get; }
    
    public IconValue Icon { get; set; }
    
    public string ToolTipText { get; }
    
    public ReactiveCommand<NavigationInformation, Unit> NavigateCommand { get; }
    
    public bool IsActive { get; }
    
    public bool IsSelected { get; }
    
    public IReadOnlyList<IContextMenuItem> AdditionalContextMenuItems { get; }

    /// <summary>What the entry accepts when something is dragged onto it; null when it is not a drop target.</summary>
    public IDropTarget? DropTarget { get; }

    /// <summary>Receives a drop that fails. Set whenever <see cref="DropTarget"/> is.</summary>
    public IErrorPresenter? ErrorPresenter { get; }
}
