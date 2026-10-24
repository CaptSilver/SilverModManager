using System.Collections.ObjectModel;
using System.Reactive;
using DynamicData;
using SilverModManager.App.UI.Controls.GameWidget;
using SilverModManager.App.UI.Controls.MiniGameWidget.ComingSoon;
using SilverModManager.App.UI.Controls.MiniGameWidget.Standard;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.UI.Sdk;
using ReactiveUI;

namespace SilverModManager.App.UI.Pages.MyGames;

public class MyGamesDesignViewModel : APageViewModel<IMyGamesViewModel>, IMyGamesViewModel
{
    public ReactiveCommand<Unit, Unit> GiveFeedbackCommand => Initializers.DisabledReactiveCommand;
    public ReactiveCommand<Unit, Unit> OpenRoadmapCommand => Initializers.DisabledReactiveCommand;
    public ReactiveCommand<Unit, Unit> AddGameFolderCommand => Initializers.DisabledReactiveCommand;
    private readonly ObservableCollection<IGameWidgetViewModel> _installedGames;
    public ReadOnlyObservableCollection<IGameWidgetViewModel> InstalledGames { get; }
    public ReadOnlyObservableCollection<IViewModelInterface> SupportedGames { get; }
    public string? LocatorProblemsNotice => null;

    public void MoveInstalledGame(int fromIndex, int toIndex) => _installedGames.Move(fromIndex, toIndex);

    public MyGamesDesignViewModel() : base(new DesignWindowManager())
    {
        var detectedGames = Enumerable.Range(0, 2)
            .Select(_ => new GameWidgetDesignViewModel())
            .ToArray();

        var supportedGames = Enumerable.Range(0, 3)
            .Select(_ => new MiniGameWidgetDesignViewModel())
            .ToList();

        _installedGames = new ObservableCollection<IGameWidgetViewModel>(detectedGames);
        InstalledGames = new ReadOnlyObservableCollection<IGameWidgetViewModel>(_installedGames);

        SupportedGames = new ReadOnlyObservableCollection<IViewModelInterface>(
            [
                new MiniGameWidgetDesignViewModel(),
                new MiniGameWidgetDesignViewModel(),
                new ComingSoonMiniGameWidgetViewModelDesignViewModel(),
            ]
        );
    }
}
