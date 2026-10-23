using System.Reactive;
using SilverModManager.UI.Sdk;
using ReactiveUI;

namespace SilverModManager.Games.FOMOD.UI;

public interface IGuidedInstallerWindowViewModel : IViewModelInterface
{
    public string WindowName { get; set; }

    /// <summary>
    /// Text shown above the steps for the whole installation, for example why the user is being asked.
    /// </summary>
    public string? Notice { get; set; }

    public IGuidedInstallerStepViewModel? ActiveStepViewModel { get; set; }

    public ReactiveCommand<Unit, Unit> CloseCommand { get; }
}
