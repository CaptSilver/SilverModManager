using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia.ReactiveUI;
using ReactiveUI;

namespace SilverModManager.App.UI.Pages.MyLoadouts;

public partial class MyLoadoutsView : ReactiveUserControl<IMyLoadoutsViewModel>
{
    public MyLoadoutsView()
    {
        InitializeComponent();

        this.WhenActivated(d =>
        {
            this.OneWayBind(ViewModel, vm => vm.GameSectionViewModels, v => v.GameSectionsItemsControl.ItemsSource)
                .DisposeWith(d);

            this.OneWayBind(ViewModel, vm => vm.IsFilteredToGame, v => v.FilterBannerStack.IsVisible)
                .DisposeWith(d);

            this.OneWayBind(ViewModel, vm => vm.FilterHeadingText, v => v.FilterHeadingTextBlock.Text)
                .DisposeWith(d);

            this.BindCommand(ViewModel, vm => vm.ShowAllGamesCommand, v => v.ShowAllGamesButton)
                .DisposeWith(d);

            this.WhenAnyValue(v => v.ViewModel!.GameSectionViewModels.Count)
                .Select(count => count == 0)
                .OnUI()
                .BindToView(this, view => view.MyLoadoutsEmptyState.IsActive);
        });
    }
}

