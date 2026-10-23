using System.Reactive;
using NexusMods.App.UI;
using NexusMods.UI.Sdk;
using ReactiveUI;

namespace NexusMods.Games.FOMOD.UI;

public class GuidedInstallerWindowDesignViewModel : AViewModel<IGuidedInstallerWindowViewModel>, IGuidedInstallerWindowViewModel
{
    public string WindowName { get; set; } = "Test FOMOD Installer";

    public string? Notice { get; set; }

    public IGuidedInstallerStepViewModel? ActiveStepViewModel { get; set; }

    public ReactiveCommand<Unit, Unit> CloseCommand => Initializers.EnabledReactiveCommand;
}
