using System.Reactive;
using SilverModManager.App.UI.Controls;
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
}
