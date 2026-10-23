using System.Reactive.Disposables;
using Avalonia.ReactiveUI;
using ReactiveUI;

namespace SilverModManager.App.UI.Pages.Sorting;

public partial class SortingSelectionView : ReactiveUserControl<ISortingSelectionViewModel>
{
    public SortingSelectionView()
    {
        InitializeComponent();
        
        this.WhenActivated(disposables =>
            {
                this.OneWayBind(ViewModel,
                        vm => vm.RulesViewModels,
                        v => v.SortOrdersTabControl.ItemsSource
                    )
                    .DisposeWith(disposables);
            }
        );
    }
}

