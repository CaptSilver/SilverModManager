using System.Reactive;
using System.Reactive.Disposables;
using SilverModManager.App.UI.Controls;
using SilverModManager.App.UI.Controls.Navigation;
using SilverModManager.UI.Sdk;
using SilverModManager.UI.Sdk.Icons;
using ReactiveUI;
using ReactiveUI.SourceGenerators;

namespace SilverModManager.App.UI.LeftMenu.Items;

public partial class LeftMenuItemDesignViewModel : AViewModel<ILeftMenuItemViewModel>, ILeftMenuItemWithToggleViewModel
{
    public StringComponent Text { get; init; } = new("Design Item");
    [Reactive] public partial IconValue Icon { get; set; } = IconValues.CogOutline;
    public string ToolTipText { get; } = "This is design time ToolTip";

    public ReactiveCommand<NavigationInformation, Unit> NavigateCommand { get; } = 
        ReactiveCommand.Create<NavigationInformation>((info) => { });
    public bool IsActive { get; } = false;
    public bool IsSelected { get; } = false;
    public IReadOnlyList<IContextMenuItem> AdditionalContextMenuItems { get; } = Array.Empty<IContextMenuItem>();
    public bool IsToggleVisible { get; set; } = false;
    
    public bool IsEnabled { get; set; } = true;
    public ReactiveCommand<Unit, Unit> ToggleIsEnabledCommand { get; } = ReactiveCommand.Create(() => { });
    
    public LeftMenuItemDesignViewModel()
    {
        this.WhenActivated(d => Text.Activate().DisposeWith(d));
    }
}
