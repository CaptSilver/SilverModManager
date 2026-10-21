using System.Collections.ObjectModel;
using System.Reactive;
using NexusMods.App.UI.Controls.GameWidget;
using NexusMods.App.UI.Controls.MiniGameWidget;
using NexusMods.App.UI.Controls.MiniGameWidget.Standard;
using NexusMods.App.UI.WorkspaceSystem;
using NexusMods.UI.Sdk;
using ReactiveUI;

namespace NexusMods.App.UI.Pages.MyGames;

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

}
