using System.Reactive;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Ids;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk;
using ReactiveUI;

namespace SilverModManager.App.UI.Pages.Diff.ApplyDiff;

/// <summary>A file a diagnostic offers to delete: its path, and the one group whose row goes.</summary>
public sealed record StrayFileNotice(GamePath Path, LoadoutItemGroupId Group)
{
    /// <summary>The name the notice lists.</summary>
    public string FileName => Path.FileName;
}

public interface IApplyDiffViewModel : IPageViewModelInterface
{
    public void Initialize(LoadoutId loadoutId);
    
    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }

    /// <summary>
    /// Removes the selected files and folders from the loadout, after asking. Disabled while nothing removable is
    /// selected and while an apply is running.
    /// </summary>
    public ReactiveCommand<Unit, Unit> RemoveSelectedCommand { get; }
    
    public IViewModelInterface? BodyViewModel { get; set; }

    /// <summary>
    /// Files in the game folder that no mod provides, as the loadout's diagnostics offer to delete them. Empty hides
    /// the notice.
    /// </summary>
    public IReadOnlyList<StrayFileNotice> StrayFiles { get; }

    /// <summary>
    /// Removes every file in <see cref="StrayFiles"/> from the loadout, after asking. Disabled while an apply is running.
    /// </summary>
    public ReactiveCommand<Unit, Unit> RemoveStrayFilesCommand { get; }

    /// <summary>True when the last attempt to build the diff tree failed, so the page can say so instead of staying blank.</summary>
    public bool LoadFailed { get; }
}
