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

    ViewLoadoutGroupFilesTreeDataGridAdapter? FileTreeAdapter { get; set; }
}
