using System.Reactive;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.UI.Sdk;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace SilverModManager.App.UI.Controls.Spine.Buttons.Icon;

public class IconButtonViewModel : AViewModel<IIconButtonViewModel>, IIconButtonViewModel
{
    [Reactive]
    public bool IsActive { get; set; }

    [Reactive] public ReactiveCommand<Unit,Unit> Click { get; set; } = Initializers.EmptyReactiveCommand;
    
    public IWorkspaceContext? WorkspaceContext { get; set; }

    [Reactive] public string Name { get; set; } = string.Empty;
}
