using System.Collections.ObjectModel;
using Avalonia.Media;
using SilverModManager.Abstractions.GuidedInstallers;
using SilverModManager.Sdk.Jobs;
using SilverModManager.UI.Sdk;

namespace SilverModManager.Games.FOMOD.UI;

public interface IGuidedInstallerStepViewModel : IViewModelInterface
{
    public string ModName { get; set; }

    /// <summary>
    /// Text shown above the step, <c>null</c> when there is none.
    /// </summary>
    public string? Notice { get; set; }

    public bool ShowInstallationCompleteScreen { get; }
    public GuidedInstallationStep? InstallationStep { get; set; }

    public ReadOnlyObservableCollection<IGuidedInstallerGroupViewModel> Groups { get; }
    public IGuidedInstallerOptionViewModel? HighlightedOptionViewModel { get; set; }
    public IImage? HighlightedOptionImage { get; }

    public Percent Progress { set; }
    public IFooterStepperViewModel FooterStepperViewModel { get; }

    public TaskCompletionSource<UserChoice>? TaskCompletionSource { get; set; }
}
