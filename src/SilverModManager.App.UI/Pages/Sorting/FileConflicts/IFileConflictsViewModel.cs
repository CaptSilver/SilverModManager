using System.ComponentModel;
using SilverModManager.UI.Sdk;
using R3;

namespace SilverModManager.App.UI.Pages.Sorting;

public interface IFileConflictsViewModel : IViewModelInterface
{
    R3.ReactiveCommand SwitchSortDirectionCommand { get; }
    
    FileConflictsTreeDataGridAdapter TreeDataGridAdapter { get; }

    IReadOnlyBindableReactiveProperty<ListSortDirection> SortDirectionCurrent { get; }

    IReadOnlyBindableReactiveProperty<bool> IsAscending { get; }

    /// <summary>One line saying which end of the list wins, worded for the game.</summary>
    IReadOnlyBindableReactiveProperty<string> OrderHint { get; }
}
