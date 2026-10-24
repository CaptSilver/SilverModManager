using System.Collections.ObjectModel;
using System.Reactive;
using SilverModManager.App.UI.Controls.GameWidget;
using SilverModManager.App.UI.Controls.MiniGameWidget;
using SilverModManager.App.UI.Controls.MiniGameWidget.Standard;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.UI.Sdk;
using ReactiveUI;

namespace SilverModManager.App.UI.Pages.MyGames;

public interface IMyGamesViewModel : IPageViewModelInterface
{
    public ReactiveCommand<Unit, Unit> OpenRoadmapCommand { get; }

    /// <summary>Picks a folder and records it as an install of a known game, showing what came of it.</summary>
    public ReactiveCommand<Unit, Unit> AddGameFolderCommand { get; }
    
    public ReadOnlyObservableCollection<IGameWidgetViewModel> InstalledGames { get; }
    
    public ReadOnlyObservableCollection<IViewModelInterface> SupportedGames { get; }

    /// <summary>
    /// What the game locators could not resolve cleanly, one line per problem, or null when there is nothing to report.
    /// </summary>
    public string? LocatorProblemsNotice { get; }

    /// <summary>
    /// Moves the installed game tile at <paramref name="fromIndex"/> to <paramref name="toIndex"/> and saves the order.
    /// </summary>
    public void MoveInstalledGame(int fromIndex, int toIndex);

}
