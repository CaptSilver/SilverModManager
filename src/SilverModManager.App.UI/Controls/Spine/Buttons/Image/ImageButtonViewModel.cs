using System.Reactive;
using System.Reactive.Disposables;
using Avalonia.Media;
using SilverModManager.App.UI.Controls.LoadoutBadge;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.UI.Sdk;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace SilverModManager.App.UI.Controls.Spine.Buttons.Image;

public class ImageButtonViewModel : AViewModel<IImageButtonViewModel>, IImageButtonViewModel
{
    [Reactive]
    public bool IsActive { get; set; }

    [Reactive] public string Name { get; set; } = "";

    [Reactive] public IImage Image { get; set; } = Initializers.IImage;

    [Reactive] public ReactiveCommand<Unit,Unit> Click { get; set; } = Initializers.EmptyReactiveCommand;
    
    public IWorkspaceContext? WorkspaceContext { get; set; }
    
    public ILoadoutBadgeViewModel? LoadoutBadgeViewModel { get; set; }

    [Reactive] public ReactiveCommand<Unit, Unit>? MoveUp { get; set; }

    [Reactive] public ReactiveCommand<Unit, Unit>? MoveDown { get; set; }
    
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
