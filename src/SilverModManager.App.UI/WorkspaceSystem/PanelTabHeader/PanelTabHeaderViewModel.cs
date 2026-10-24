using System.Reactive;
using SilverModManager.UI.Sdk.Icons;
using SilverModManager.App.UI.Resources;
using SilverModManager.UI.Sdk;
using ReactiveUI;
using ReactiveUI.SourceGenerators;

namespace SilverModManager.App.UI.WorkspaceSystem;

public partial class PanelTabHeaderViewModel : AViewModel<IPanelTabHeaderViewModel>, IPanelTabHeaderViewModel
{
    public PanelTabId Id { get; }

    [Reactive]
    public partial string Title { get; set; } = Language.PanelTabHeaderViewModel_Title_New_Tab;

    [Reactive] public partial IconValue Icon { get; set; } = new();

    [Reactive] public partial bool CanClose { get; set; }

    [Reactive] public partial bool IsSelected { get; set; } = true;

    public ReactiveCommand<Unit, PanelTabId> CloseTabCommand { get; }

    public PanelTabHeaderViewModel(PanelTabId id)
    {
        Id = id;
        CloseTabCommand = ReactiveCommand.Create(() => Id, this.WhenAnyValue(vm => vm.CanClose));
    }
}
