using System.Reactive;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.UI.Sdk;
using ReactiveUI;
using ReactiveUI.SourceGenerators;

namespace SilverModManager.App.UI.Controls.Spine.Buttons.Icon;

public partial class IconButtonViewModel : AViewModel<IIconButtonViewModel>, IIconButtonViewModel
{
    [Reactive]
    public partial bool IsActive { get; set; }

    [Reactive] public partial ReactiveCommand<Unit,Unit> Click { get; set; } = Initializers.EmptyReactiveCommand;
    
    public IWorkspaceContext? WorkspaceContext { get; set; }

    [Reactive] public partial string Name { get; set; } = string.Empty;
}
