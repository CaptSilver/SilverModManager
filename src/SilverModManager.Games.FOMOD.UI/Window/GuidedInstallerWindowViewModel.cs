using System.Reactive;
using JetBrains.Annotations;
using SilverModManager.App.UI;
using SilverModManager.UI.Sdk;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace SilverModManager.Games.FOMOD.UI;

[UsedImplicitly]
public class GuidedInstallerWindowViewModel : AViewModel<IGuidedInstallerWindowViewModel>, IGuidedInstallerWindowViewModel
{
    [Reactive] public string WindowName { get; set; } = string.Empty;

    [Reactive] public string? Notice { get; set; }

    [Reactive]
    public IGuidedInstallerStepViewModel? ActiveStepViewModel { get; set; }

    public ReactiveCommand<Unit, Unit> CloseCommand => ReactiveCommand.Create(() => { });
}
