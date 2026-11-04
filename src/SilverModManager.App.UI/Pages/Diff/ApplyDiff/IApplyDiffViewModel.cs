using System.Reactive;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Ids;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.App.UI.Resources;
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

/// <summary>A file an Apply cannot write, as the preview's notice lists it.</summary>
public sealed record UnwritableFileNotice(UnwritableFile File)
{
    /// <summary>The line the notice shows.</summary>
    public string Text => string.Format(
        File.Reason == UnwritableReason.NotOnDisk ? Language.ApplyDiff_Unwritable_NotOnDisk : Language.ApplyDiff_Unwritable_DifferentOnDisk,
        File.Path.Path);
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

    /// <summary>
    /// Files the loadout names that no Apply can write, because no copy of them is kept. Empty hides the notice.
    /// They are not pending work: the tree marks them, and the status bar and Apply button do not count them.
    /// </summary>
    public IReadOnlyList<UnwritableFileNotice> UnwritableFiles { get; }

    /// <summary>
    /// Accepts the file the game folder has for each unwritable file that is on disk: it is backed up and becomes the
    /// loadout's version of the path. Disabled while none is on disk and while an apply is running.
    /// </summary>
    public ReactiveCommand<Unit, Unit> KeepDiskFilesCommand { get; }

    /// <summary>
    /// Drops each unwritable file that is not on disk from what the loadout wants. Disabled while none is missing from
    /// disk and while an apply is running.
    /// </summary>
    public ReactiveCommand<Unit, Unit> ForgetFilesCommand { get; }

    /// <summary>True when the last attempt to build the diff tree failed, so the page can say so instead of staying blank.</summary>
    public bool LoadFailed { get; }
}
