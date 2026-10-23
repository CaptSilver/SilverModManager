using System.Reactive;
using SilverModManager.App.UI.Controls.Navigation;
using SilverModManager.UI.Sdk;
using ReactiveUI;

namespace SilverModManager.App.UI.LeftMenu.Items;

public interface IApplyControlViewModel : IViewModelInterface
{
    ReactiveCommand<Unit,Unit> ApplyCommand { get; }
    
    ReactiveCommand<NavigationInformation, Unit> ShowApplyDiffCommand { get; }
    
    ILaunchButtonViewModel LaunchButtonViewModel { get; }
    
    bool IsLaunchButtonEnabled { get; }
    
    bool IsProcessing { get; }
    
    bool IsApplying { get; }
    
    string ApplyButtonText { get; }
    
    string ProcessingText { get; }

    /// <summary>
    /// How far the current phase of an apply has got, in [0, 1]. Meaningless while <see cref="IsProgressIndeterminate"/>.
    /// </summary>
    double ProgressFraction { get; }

    /// <summary>
    /// Whether the current phase cannot say how much is left.
    /// </summary>
    bool IsProgressIndeterminate { get; }
}
