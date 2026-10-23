using System.Reactive;
using SilverModManager.UI.Sdk;
using SilverModManager.UI.Sdk.Icons;
using ReactiveUI;

namespace SilverModManager.App.UI.WorkspaceSystem;

public interface IPanelTabHeaderViewModel : IViewModelInterface
{
    public PanelTabId Id { get; }

    public string Title { get; set; }

    public IconValue Icon { get; set; }

    public bool IsSelected { get; set;  }

    public bool CanClose { get; set; }

    public ReactiveCommand<Unit, PanelTabId> CloseTabCommand { get; }
}
