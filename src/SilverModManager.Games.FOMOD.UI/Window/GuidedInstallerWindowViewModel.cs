using System.Reactive;
using JetBrains.Annotations;
using SilverModManager.App.UI;
using SilverModManager.UI.Sdk;
using ReactiveUI;
using ReactiveUI.SourceGenerators;

namespace SilverModManager.Games.FOMOD.UI;

[UsedImplicitly]
public partial class GuidedInstallerWindowViewModel : AViewModel<IGuidedInstallerWindowViewModel>, IGuidedInstallerWindowViewModel
{
    [Reactive] public partial string WindowName { get; set; } = string.Empty;

    [Reactive] public partial string? Notice { get; set; }

    [Reactive]
    public partial IGuidedInstallerStepViewModel? ActiveStepViewModel { get; set; }

    public ReactiveCommand<Unit, Unit> CloseCommand => ReactiveCommand.Create(() => { });
}
