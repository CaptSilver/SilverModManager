using SilverModManager.App.UI.Overlays;
using SilverModManager.UI.Sdk;

namespace SilverModManager.App.UI.Windows;

public interface IMainWindowViewModel : IViewModelInterface, IWorkspaceWindow
{
    IOverlayViewModel? CurrentOverlay { get; }
}
