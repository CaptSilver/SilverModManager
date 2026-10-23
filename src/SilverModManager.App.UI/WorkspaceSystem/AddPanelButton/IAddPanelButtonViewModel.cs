using System.Reactive;
using Avalonia.Media;
using SilverModManager.UI.Sdk;
using ReactiveUI;

namespace SilverModManager.App.UI.WorkspaceSystem;

public interface IAddPanelButtonViewModel : IViewModelInterface
{
    public WorkspaceGridState NewLayoutState { get; }

    public IImage ButtonImage { get; }

    public ReactiveCommand<Unit, WorkspaceGridState> AddPanelCommand { get; }
}
