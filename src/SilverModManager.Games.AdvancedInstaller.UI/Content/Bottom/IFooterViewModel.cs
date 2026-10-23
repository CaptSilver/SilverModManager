using System.Reactive;
using SilverModManager.UI.Sdk;
using ReactiveUI;

namespace SilverModManager.Games.AdvancedInstaller.UI;

public interface IFooterViewModel : IViewModelInterface
{
    /// <summary>
    /// Determines whether the Install button is enabled or not.
    /// </summary>
    public bool CanInstall { get; set; }

    public ReactiveCommand<Unit, Unit> CancelCommand { get;  }

    public ReactiveCommand<Unit, Unit> InstallCommand { get; }
}
