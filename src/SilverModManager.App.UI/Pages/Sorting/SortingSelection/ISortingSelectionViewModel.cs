using System.Collections.ObjectModel;
using SilverModManager.App.UI.Controls.Navigation;
using SilverModManager.UI.Sdk;
using R3;

namespace SilverModManager.App.UI.Pages.Sorting;

public interface ISortingSelectionViewModel : IViewModelInterface
{
    ReadOnlyObservableCollection<IViewModelInterface> RulesViewModels { get; }

    public IReadOnlyBindableReactiveProperty<bool> CanEdit { get; }
    
    public ReactiveCommand<NavigationInformation> OpenAllModsLoadoutPageCommand { get; }
}
