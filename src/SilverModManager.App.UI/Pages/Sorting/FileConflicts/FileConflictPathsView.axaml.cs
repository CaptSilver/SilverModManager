using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia.ReactiveUI;
using JetBrains.Annotations;
using ReactiveUI;

namespace SilverModManager.App.UI.Pages.Sorting;

[UsedImplicitly]
public partial class FileConflictPathsView : ReactiveUserControl<IFileConflictPathsViewModel>
{
    public FileConflictPathsView()
    {
        InitializeComponent();

        this.WhenActivated(disposables =>
        {
            if (ViewModel is not { } viewModel) return;

            // Rows are replaced as a whole after a pin change, which finishes off the UI thread.
            viewModel.WhenAnyValue(vm => vm.Rows)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(rows => PathsList.ItemsSource = rows)
                .DisposeWith(disposables);
        });
    }
}
