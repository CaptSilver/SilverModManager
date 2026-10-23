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
    public ReadOnlyObservableCollection<IGameWidgetViewModel> InstalledGames { get; }
    public ReadOnlyObservableCollection<IViewModelInterface> SupportedGames { get; }
    public string? LocatorProblemsNotice => null;

    public MyGamesDesignViewModel() : base(new DesignWindowManager())
    {
        var detectedGames = Enumerable.Range(0, 2)
            .Select(_ => new GameWidgetDesignViewModel())
            .ToArray();

        var supportedGames = Enumerable.Range(0, 3)
            .Select(_ => new MiniGameWidgetDesignViewModel())
            .ToList();

        InstalledGames = new ReadOnlyObservableCollection<IGameWidgetViewModel>(new ObservableCollection<IGameWidgetViewModel>(detectedGames));

        SupportedGames = new ReadOnlyObservableCollection<IViewModelInterface>(
            [
                new MiniGameWidgetDesignViewModel(),
                new MiniGameWidgetDesignViewModel(),
                new ComingSoonMiniGameWidgetViewModelDesignViewModel(),
            ]
        );
    }
}
