using System.Collections.ObjectModel;
using SilverModManager.App.UI.Pages.MyLoadouts.GameLoadoutsSectionEntry;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceSystem;

namespace SilverModManager.App.UI.Pages.MyLoadouts;

public class MyLoadoutsDesignViewModel : APageViewModel<IMyLoadoutsViewModel>, IMyLoadoutsViewModel
{

    public MyLoadoutsDesignViewModel() : base(new DesignWindowManager())
    {
    }

    public MyLoadoutsDesignViewModel(IWindowManager windowManager) : base(windowManager)
    {
    }

    public ReadOnlyObservableCollection<IGameLoadoutsSectionEntryViewModel> GameSectionViewModels { get; } = new([
            new GameLoadoutsSectionEntryDesignViewModel(),
            new GameLoadoutsSectionEntryDesignViewModel(),
        ]
    );
}
