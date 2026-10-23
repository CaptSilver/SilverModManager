using NexusMods.App.UI.Controls.Navigation;
using NexusMods.App.UI.WorkspaceSystem;

namespace NexusMods.App.UI.Pages.LoadoutGroupFilesPage;

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
    
    ViewLoadoutGroupFilesTreeDataGridAdapter? FileTreeAdapter { get; set; }
}
