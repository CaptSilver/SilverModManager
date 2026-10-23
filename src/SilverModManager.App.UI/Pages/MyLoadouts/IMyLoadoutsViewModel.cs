using System.Collections.ObjectModel;
using SilverModManager.App.UI.Pages.MyLoadouts.GameLoadoutsSectionEntry;
using SilverModManager.App.UI.WorkspaceSystem;

namespace SilverModManager.App.UI.Pages.MyLoadouts;


public interface IMyLoadoutsViewModel : IPageViewModelInterface
{
    ReadOnlyObservableCollection<IGameLoadoutsSectionEntryViewModel> GameSectionViewModels { get; }
}
