using System.Reactive;
using Avalonia.Media;
using SilverModManager.UI.Sdk;
using ReactiveUI;

namespace SilverModManager.App.UI.WorkspaceSystem;

public class AddPanelButtonViewModel : AViewModel<IAddPanelButtonViewModel>, IAddPanelButtonViewModel
{
    public WorkspaceGridState NewLayoutState { get; }
    public IImage ButtonImage { get; }
    public ReactiveCommand<Unit, WorkspaceGridState> AddPanelCommand { get; }

    public AddPanelButtonViewModel(
        WorkspaceGridState newLayoutState,
        IImage buttonImage)
    {
        NewLayoutState = newLayoutState;
        ButtonImage = buttonImage;

        AddPanelCommand = ReactiveCommand.Create(() => NewLayoutState);
    }
}
