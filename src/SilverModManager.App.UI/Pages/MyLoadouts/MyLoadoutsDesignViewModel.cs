using System.Collections.ObjectModel;
using System.Reactive;
using SilverModManager.App.UI.Pages.MyLoadouts.GameLoadoutsSectionEntry;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceSystem;
using ReactiveUI;

namespace SilverModManager.App.UI.Pages.MyLoadouts;

public class MyLoadoutsDesignViewModel : APageViewModel<IMyLoadoutsViewModel>, IMyLoadoutsViewModel
{

    public MyLoadoutsDesignViewModel() : base(new DesignWindowManager())
    {
    }

    public MyLoadoutsDesignViewModel(IWindowManager windowManager) : base(windowManager)
    {
    }

    public bool IsFilteredToGame => false;
    public string FilterHeadingText => "";
    public ReactiveCommand<Unit, Unit> ShowAllGamesCommand { get; } = ReactiveCommand.Create(() => { });

    public ReadOnlyObservableCollection<IGameLoadoutsSectionEntryViewModel> GameSectionViewModels { get; } = new([
            new GameLoadoutsSectionEntryDesignViewModel(),
            new GameLoadoutsSectionEntryDesignViewModel(),
        ]
    );
}
