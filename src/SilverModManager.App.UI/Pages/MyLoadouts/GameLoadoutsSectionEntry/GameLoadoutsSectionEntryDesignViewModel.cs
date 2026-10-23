using System.Collections.ObjectModel;
using System.Reactive;
using SilverModManager.App.UI.Controls.LoadoutCard;
using SilverModManager.UI.Sdk;
using ReactiveUI;

namespace SilverModManager.App.UI.Pages.MyLoadouts.GameLoadoutsSectionEntry;

public class GameLoadoutsSectionEntryDesignViewModel : AViewModel<IGameLoadoutsSectionEntryViewModel>, IGameLoadoutsSectionEntryViewModel
{
    public string HeadingText { get; } = "Stardew Valley Loadouts";

    public ReadOnlyObservableCollection<IViewModelInterface> CardViewModels { get; } = new([
            new CreateNewLoadoutCardViewModel()
            {
                AddLoadoutCommand = ReactiveCommand.Create(() => { }),
            },
            new LoadoutCardDesignViewModel(),
            new LoadoutCardDesignViewModel(),
        ]
    );
    
    void IDisposable.Dispose() { }
}
