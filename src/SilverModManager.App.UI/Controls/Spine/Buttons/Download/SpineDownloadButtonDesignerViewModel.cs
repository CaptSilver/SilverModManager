using System.Reactive;
using DynamicData.Kernel;
using SilverModManager.App.UI.Resources;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.Sdk.Jobs;
using SilverModManager.UI.Sdk;
using ReactiveUI;
using ReactiveUI.SourceGenerators;

namespace SilverModManager.App.UI.Controls.Spine.Buttons.Download;

public partial class SpineDownloadButtonDesignerViewModel : AViewModel<ISpineDownloadButtonViewModel>, ISpineDownloadButtonViewModel
{
    [Reactive]
    public partial double Number { get; set; } = 4.2f;

    [Reactive]
    public partial string Units { get; set; } = "MB/s";

    [Reactive]
    public partial Optional<Percent> Progress { get; set; }

    [Reactive]
    public partial ReactiveCommand<Unit,Unit> Click { get; set; }

    public IWorkspaceContext? WorkspaceContext { get; set; }

    [Reactive]
    public partial bool IsActive { get; set; }

    public string ToolTip { get; set; } = Language.SpineDownloadButton_ToolTip;

    public SpineDownloadButtonDesignerViewModel()
    {
        Click = ReactiveCommand.CreateFromTask(StartProgress);
    }

    private async Task StartProgress()
    {
        if (Progress.HasValue) return;
        foreach (var i in Enumerable.Range(0, 100))
        {
            Progress = new Percent(i / 100d);
            Number = Random.Shared.NextSingle() * 10f;
            await Task.Delay(100);
        }
        Progress = Optional<Percent>.None;
    }
}
