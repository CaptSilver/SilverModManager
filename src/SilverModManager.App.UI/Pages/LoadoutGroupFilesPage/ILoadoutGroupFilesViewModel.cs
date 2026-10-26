using SilverModManager.App.UI.Controls.Navigation;
using SilverModManager.App.UI.WorkspaceSystem;

namespace SilverModManager.App.UI.Pages.LoadoutGroupFilesPage;

public interface ILoadoutGroupFilesViewModel : IPageViewModelInterface
{
    LoadoutGroupFilesPageContext? Context { get; set; }

    R3.ReactiveCommand<NavigationInformation> OpenEditorCommand { get; }

    R3.ReactiveCommand<R3.Unit> RemoveCommand { get; }

    /// <summary>
    /// Gives the selected file back to the game when it was claimed with "Treat as my file". Only runnable
    /// for a file a claim exists for.
    /// </summary>
    R3.ReactiveCommand<R3.Unit> UndoTreatAsMineCommand { get; }
    
    /// <summary>
    /// Reads the game folder again and drops the External Changes whose files are gone. Only runnable on the
    /// loadout's External Changes, not on a mod's file list.
    /// </summary>
    R3.ReactiveCommand<R3.Unit> RefreshCommand { get; }

    /// <summary>Whether the page shows the loadout's External Changes, the only place the move commands apply.</summary>
    bool IsExternalChanges { get; }

    /// <summary>Moves the selected files out of External Changes into a mod made for them.</summary>
    R3.ReactiveCommand<R3.Unit> MoveToNewModCommand { get; }

    /// <summary>Moves the selected files out of External Changes into one of the loadout's mods.</summary>
    R3.ReactiveCommand<R3.Unit> MoveToExistingModCommand { get; }

    ViewLoadoutGroupFilesTreeDataGridAdapter? FileTreeAdapter { get; set; }
}
