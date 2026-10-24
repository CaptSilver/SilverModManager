using System.Reactive;
using System.Reactive.Disposables;
using Avalonia.Media;
using SilverModManager.App.UI.Controls.LoadoutBadge;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.UI.Sdk;
using ReactiveUI;
using ReactiveUI.SourceGenerators;

namespace SilverModManager.App.UI.Controls.Spine.Buttons.Image;

public partial class ImageButtonViewModel : AViewModel<IImageButtonViewModel>, IImageButtonViewModel
{
    [Reactive]
    public partial bool IsActive { get; set; }

    [Reactive] public partial string Name { get; set; } = "";

    [Reactive] public partial IImage Image { get; set; } = Initializers.IImage;

    [Reactive] public partial ReactiveCommand<Unit,Unit> Click { get; set; } = Initializers.EmptyReactiveCommand;
    
    public IWorkspaceContext? WorkspaceContext { get; set; }
    
    public ILoadoutBadgeViewModel? LoadoutBadgeViewModel { get; set; }

    [Reactive] public partial ReactiveCommand<Unit, Unit>? MoveUp { get; set; }

    [Reactive] public partial ReactiveCommand<Unit, Unit>? MoveDown { get; set; }
    
    public ImageButtonViewModel()
    {
        this.WhenActivated(d =>
        {
            this.WhenAnyValue(vm => vm.IsActive)
                .SubscribeWithErrorLogging(isActive =>
                {
                    if (LoadoutBadgeViewModel != null)
                    {
                        LoadoutBadgeViewModel.IsLoadoutSelected = isActive;
                    }
                })
                .DisposeWith(d);
        });
    }
}
