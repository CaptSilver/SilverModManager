using System.Reactive.Disposables;
using Avalonia.ReactiveUI;
using ReactiveUI;

namespace SilverModManager.App.UI.Pages.Collections.Update;

public partial class CollectionUpdateProgressView : ReactiveUserControl<ICollectionUpdateProgressViewModel>
{
    public CollectionUpdateProgressView()
    {
        InitializeComponent();

        this.WhenActivated(disposables =>
        {
            this.OneWayBind(ViewModel, vm => vm.Text, view => view.ProgressText.Text).DisposeWith(disposables);
        });
    }
}
