using System.Collections.ObjectModel;
using Avalonia.Media;
using NexusMods.Abstractions.GuidedInstallers;
using NexusMods.Sdk.Jobs;
using NexusMods.UI.Sdk;

namespace NexusMods.Games.FOMOD.UI;

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
