using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using DynamicData;
using DynamicData.Kernel;
using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.HttpDownloads;
using SilverModManager.App.UI.Resources;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.Sdk.Jobs;
using SilverModManager.UI.Sdk;
using ReactiveUI;
using ReactiveUI.SourceGenerators;

namespace SilverModManager.App.UI.Controls.Spine.Buttons.Download;

[UsedImplicitly]
public partial class SpineDownloadButtonViewModel : AViewModel<ISpineDownloadButtonViewModel>, ISpineDownloadButtonViewModel
{
    public SpineDownloadButtonViewModel(IJobMonitor jobMonitor, ILogger<SpineDownloadButtonViewModel> logger)
    {
        this.WhenActivated(disposables =>
        {

            jobMonitor.ObserveActiveJobs<IHttpDownloadJob>()
                .AverageProgressPercent()
                .OnUI()
                .Subscribe(rate => { Progress = rate.Value > 0 ? rate : Optional<Percent>.None; })
                .DisposeWith(disposables);
            
            jobMonitor.ObserveActiveJobs<IHttpDownloadJob>()
                .SumRateOfProgress()
                .OnUI()
                // Convert from bytes to MB/s
                // TODO: use Humanizer
                .Subscribe(rate => Number = rate / 1024 / 1024)
                .DisposeWith(disposables);
            
        });
    }

    [Reactive] public partial double Number { get; set; }

    [Reactive] public partial string Units { get; set; } = "MB/s";

    [Reactive] public partial Optional<Percent> Progress { get; set; }

    [Reactive] public partial ReactiveCommand<Unit,Unit> Click { get; set; } = Initializers.EmptyReactiveCommand;
    
    public IWorkspaceContext? WorkspaceContext { get; set; }

    [Reactive] public partial bool IsActive { get; set; }

    [Reactive] public partial string ToolTip { get; set; } = Language.SpineDownloadButton_ToolTip;
}
