using System.ComponentModel;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.UI.Sdk;
using R3;

namespace SilverModManager.App.UI.Pages.Sorting;

public interface IFileConflictsViewModel : IViewModelInterface
{
    R3.ReactiveCommand SwitchSortDirectionCommand { get; }
    
    FileConflictsTreeDataGridAdapter TreeDataGridAdapter { get; }

    /// <summary>Moves a row by places on the screen, for the pad; null on a view model that cannot reorder.</summary>
    SilverModManager.App.UI.Input.Reorder.IReorderable<EntityId>? Reorderable => null;

    IReadOnlyBindableReactiveProperty<ListSortDirection> SortDirectionCurrent { get; }

    IReadOnlyBindableReactiveProperty<bool> IsAscending { get; }

    /// <summary>One line saying which end of the list wins, worded for the game.</summary>
    IReadOnlyBindableReactiveProperty<string> OrderHint { get; }
}
