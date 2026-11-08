using System.Reactive.Disposables;
using Avalonia.ReactiveUI;
using ReactiveUI;

namespace SilverModManager.App.UI.Pages.Collections.Update;

public partial class CollectionUpdateReviewView : ReactiveUserControl<ICollectionUpdateReviewViewModel>
{
    public CollectionUpdateReviewView()
    {
        InitializeComponent();

        this.WhenActivated(disposables =>
        {
            this.OneWayBind(ViewModel, vm => vm.Sections, view => view.SectionsList.ItemsSource).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.Warnings, view => view.WarningsList.ItemsSource).DisposeWith(disposables);

            this.OneWayBind(ViewModel, vm => vm.Note, view => view.NoteText.Text).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.Note, view => view.NoteText.IsVisible, static note => !string.IsNullOrEmpty(note)).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.DiskNote, view => view.DiskNoteText.Text).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.DiskNote, view => view.DiskNoteText.IsVisible, static note => !string.IsNullOrEmpty(note)).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.RestorePointText, view => view.RestorePointTextBlock.Text).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.RestorePointText, view => view.RestorePointTextBlock.IsVisible, static text => !string.IsNullOrEmpty(text)).DisposeWith(disposables);

            this.OneWayBind(ViewModel, vm => vm.CanSkipMissing, view => view.SkipMissingBox.IsVisible).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.SkipMissing, view => view.SkipMissingBox.IsChecked).DisposeWith(disposables);
        });
    }
}
