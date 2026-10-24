using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using SilverModManager.App.UI;
using SilverModManager.Sdk.Jobs;
using SilverModManager.UI.Sdk;
using ReactiveUI;
using ReactiveUI.SourceGenerators;

namespace SilverModManager.Games.FOMOD.UI;

public partial class FooterStepperViewModel : AViewModel<IFooterStepperViewModel>, IFooterStepperViewModel
{
    [Reactive]
    public partial bool IsLastStep { get; private set; }

    [Reactive]
    public partial Percent Progress { get; set; } = Percent.Zero;

    [Reactive] public partial bool CanGoNext { get; set; }
    [Reactive] public partial bool CanGoPrev { get; set; }
    public ReactiveCommand<Unit, Unit> GoToNextCommand { get; protected init; }
    public ReactiveCommand<Unit, Unit> GoToPrevCommand { get; protected init; }

    public FooterStepperViewModel()
    {
        GoToNextCommand = ReactiveCommand.Create(() => {}, this.WhenAnyValue(vm => vm.CanGoNext));
        GoToPrevCommand = ReactiveCommand.Create(() => {}, this.WhenAnyValue(vm => vm.CanGoPrev));

        this.WhenActivated(disposables =>
        {
            this.WhenAnyValue(vm => vm.Progress)
                .Select(progress => progress == Percent.One)
                .BindToVM(this, vm => vm.IsLastStep)
                .DisposeWith(disposables);
        });
    }
}
