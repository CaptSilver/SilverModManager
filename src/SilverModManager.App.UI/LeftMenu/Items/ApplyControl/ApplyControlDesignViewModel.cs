using System.Reactive;
using SilverModManager.App.UI.Controls.Navigation;
using SilverModManager.App.UI.Resources;
using SilverModManager.UI.Sdk;
using ReactiveUI;
using ReactiveUI.SourceGenerators;

namespace SilverModManager.App.UI.LeftMenu.Items;

public partial class ApplyControlDesignViewModel : AViewModel<IApplyControlViewModel>, IApplyControlViewModel
{
    public ReactiveCommand<Unit, Unit> ApplyCommand { get; }
    public ReactiveCommand<Unit, Unit> IngestCommand { get; }

    public ReactiveCommand<NavigationInformation, Unit> ShowApplyDiffCommand { get; }
    [Reactive] public partial bool CanApply { get; private set; } = true;
    [Reactive] public partial bool IsApplying { get; private set; } = false;
    public bool IsIngesting { get; private set; }

    public ILaunchButtonViewModel LaunchButtonViewModel { get; } = new LaunchButtonDesignViewModel();
    public bool IsLaunchButtonEnabled { get; } = true;
    public bool IsProcessing { get; } = false;
    public string ApplyButtonText { get; } = Language.ApplyControlViewModel__APPLY;

    public string ProcessingText { get; } = "PROCESSING TEXT";
    public double ProgressFraction { get; } = 0.4;
    public bool IsProgressIndeterminate { get; } = false;

    public ApplyControlDesignViewModel()
    {
        ShowApplyDiffCommand = ReactiveCommand.Create<NavigationInformation>(_ => { });

        ApplyCommand = ReactiveCommand.CreateFromTask(async () =>
            {
                IsApplying = true;
                CanApply = false;

                await Task.Delay(3000);

                IsApplying = false;
                CanApply = true;
            }
        );

        IngestCommand = ReactiveCommand.CreateFromTask(async () =>
            {
                IsIngesting = true;
                CanApply = false;

                await Task.Delay(3000);

                IsIngesting = false;
                CanApply = true;
            }
        );
    }
}
