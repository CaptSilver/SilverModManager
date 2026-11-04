using System.Reactive;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk;
using ReactiveUI;

namespace SilverModManager.App.UI.Pages.Diff.ApplyDiff;

public class ApplyDiffDesignViewModel : APageViewModel<IApplyDiffViewModel>, IApplyDiffViewModel
{
    public ApplyDiffDesignViewModel(IWindowManager windowManager) : base(windowManager)
    {
    }
    
    public ApplyDiffDesignViewModel() : base(new DesignWindowManager())
    {
    }

    public void Initialize(LoadoutId loadoutId)
    {
        throw new NotImplementedException();
    }

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; } = ReactiveCommand.Create(() => { });
    public ReactiveCommand<Unit, Unit> RemoveSelectedCommand { get; } = ReactiveCommand.Create(() => { });
    public IViewModelInterface? BodyViewModel { get; set; }
    public bool LoadFailed => false;

    public IReadOnlyList<StrayFileNotice> StrayFiles { get; } =
    [
        new(new GamePath(LocationId.Game, "Data/SKSE/Plugins/SkyrimUpscaler.dll"), default),
        new(new GamePath(LocationId.Game, "Data/SKSE/Plugins/old_plugin.dll"), default),
    ];

    public ReactiveCommand<Unit, Unit> RemoveStrayFilesCommand { get; } = ReactiveCommand.Create(() => { });

    public IReadOnlyList<UnwritableFileNotice> UnwritableFiles { get; } =
    [
        new(new UnwritableFile(new GamePath(LocationId.Game, "Data/Textures/old.dds"), UnwritableReason.NotOnDisk)),
        new(new UnwritableFile(new GamePath(LocationId.Game, "Data/Meshes/body.nif"), UnwritableReason.DifferentOnDisk)),
    ];

    public ReactiveCommand<Unit, Unit> KeepDiskFilesCommand { get; } = ReactiveCommand.Create(() => { });
    public ReactiveCommand<Unit, Unit> ForgetFilesCommand { get; } = ReactiveCommand.Create(() => { });
}
