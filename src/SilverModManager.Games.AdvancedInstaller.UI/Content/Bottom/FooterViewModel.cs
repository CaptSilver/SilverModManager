using System.Reactive;
using SilverModManager.UI.Sdk;
using ReactiveUI;
using ReactiveUI.SourceGenerators;

namespace SilverModManager.Games.AdvancedInstaller.UI;

internal partial class FooterViewModel : AViewModel<IFooterViewModel>, IFooterViewModel
{
    [Reactive] public partial bool CanInstall { get; set; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }
    public ReactiveCommand<Unit, Unit> InstallCommand { get; }

    public FooterViewModel()
    {
        CancelCommand = ReactiveCommand.Create(() => { });
        InstallCommand = ReactiveCommand.Create(() => { },
            this.WhenAnyValue(x => x.CanInstall));
    }
}
