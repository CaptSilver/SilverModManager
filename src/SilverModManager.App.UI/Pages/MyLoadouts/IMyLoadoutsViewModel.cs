using System.Collections.ObjectModel;
using System.Reactive;
using SilverModManager.App.UI.Pages.MyLoadouts.GameLoadoutsSectionEntry;
using SilverModManager.App.UI.WorkspaceSystem;
using ReactiveUI;

namespace SilverModManager.App.UI.Pages.MyLoadouts;


public interface IMyLoadoutsViewModel : IPageViewModelInterface
{
    ReadOnlyObservableCollection<IGameLoadoutsSectionEntryViewModel> GameSectionViewModels { get; }

    /// <summary>
    /// Whether the page lists a single game's loadouts rather than every game's.
    /// </summary>
    bool IsFilteredToGame { get; }

    /// <summary>
    /// Names the game the page is filtered to; empty when it is not filtered.
    /// </summary>
    string FilterHeadingText { get; }

    /// <summary>
    /// Replaces this page with the unfiltered one.
    /// </summary>
    ReactiveCommand<Unit, Unit> ShowAllGamesCommand { get; }
}
