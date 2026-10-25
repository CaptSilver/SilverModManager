using System.Reactive.Disposables;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.ReactiveUI;
using JetBrains.Annotations;
using ReactiveUI;
using SilverModManager.App.UI.Controls.Cards;

namespace SilverModManager.App.UI.Pages.NexusCatalog;

[UsedImplicitly]
public partial class NexusModsView : ReactiveUserControl<INexusModsViewModel>
{
    public NexusModsView()
    {
        InitializeComponent();

        this.WhenActivated(disposables =>
        {
            if (ViewModel is not { } viewModel) return;

            this.Bind(ViewModel, vm => vm.SearchText, view => view.SearchBox.Text).DisposeWith(disposables);

            SortBox.Options = NexusSortChoices.Mods;
            SortBox.BindSort(viewModel.WhenAnyValue(vm => vm.Sort), sort => viewModel.Sort = sort).DisposeWith(disposables);

            viewModel.WhenAnyValue(vm => vm.ErrorText)
                .Subscribe(text =>
                {
                    ErrorText.Text = text;
                    ErrorText.IsVisible = text != "";
                })
                .DisposeWith(disposables);

            ModGrid.Thumbnails = viewModel.Thumbnails;
            viewModel.WhenAnyValue(vm => vm.Mods)
                .Subscribe(mods => ModGrid.Items = mods)
                .DisposeWith(disposables);

            BindCollections(viewModel.Collections, disposables);
        });
    }

    private void BindCollections(INexusCollectionsViewModel collections, CompositeDisposable disposables)
    {
        this.Bind(ViewModel, vm => vm.Collections.SearchText, view => view.CollectionSearchBox.Text).DisposeWith(disposables);

        CollectionSortBox.Options = NexusSortChoices.Collections;
        CollectionSortBox.BindSort(collections.WhenAnyValue(vm => vm.Sort), sort => collections.Sort = sort).DisposeWith(disposables);

        collections.WhenAnyValue(vm => vm.ErrorText)
            .Subscribe(text =>
            {
                CollectionErrorText.Text = text;
                CollectionErrorText.IsVisible = text != "";
            })
            .DisposeWith(disposables);

        CollectionGrid.Thumbnails = collections.Thumbnails;
        collections.WhenAnyValue(vm => vm.Collections)
            .Subscribe(rows => CollectionGrid.Items = rows)
            .DisposeWith(disposables);

        // Only the selected tab is on screen, so this is what tells the view model when to start searching.
        Tabs.SelectionChanged += OnTabChanged;
        Disposable.Create(() => Tabs.SelectionChanged -= OnTabChanged).DisposeWith(disposables);
        collections.IsVisible = ReferenceEquals(Tabs.SelectedItem, CollectionsTab);
    }

    private void OnTabChanged(object? sender, SelectionChangedEventArgs e)
    {
        // The sort boxes inside a tab raise SelectionChanged too, and it bubbles up to here.
        if (e.Source != Tabs || ViewModel is not { } viewModel) return;
        viewModel.Collections.IsVisible = ReferenceEquals(Tabs.SelectedItem, CollectionsTab);
    }

    private void OnCollectionsEndReached(object? sender, EventArgs e)
    {
        if (ViewModel is not { Collections.HasMore: true } viewModel) return;
        var command = (System.Windows.Input.ICommand)viewModel.Collections.LoadMoreCommand;
        if (command.CanExecute(null)) command.Execute(null);
    }

    private static NexusCollectionRow? CollectionOf(object? sender) => (sender as Control)?.DataContext as NexusCollectionRow;

    // Same reason as OnInstallClick: ICommand.Execute alone would let a double click run the install twice.
    private void OnCollectionPrimaryClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel || CollectionOf(sender) is not { } row) return;
        var command = (System.Windows.Input.ICommand)viewModel.Collections.PrimaryCommand;
        if (command.CanExecute(row)) command.Execute(row);
    }

    private void OnCollectionOpenClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } viewModel && CollectionOf(sender) is { } row) viewModel.Collections.OpenOnNexusCommand.Execute(row).Subscribe();
    }

    // The grid reports the end on every scroll step near it, and again when a page leaves the view unfilled; the
    // command is disabled while a page loads, and the view model ignores the call once the catalog is exhausted.
    private void OnEndReached(object? sender, EventArgs e)
    {
        if (ViewModel is not { HasMore: true } viewModel) return;
        var command = (System.Windows.Input.ICommand)viewModel.LoadMoreCommand;
        if (command.CanExecute(null)) command.Execute(null);
    }

    private static NexusModsModRow? RowOf(object? sender) => (sender as Control)?.DataContext as NexusModsModRow;

    // ReactiveCommand's ICommand.Execute doesn't look at CanExecute itself, and the command is disabled while it runs,
    // so asking first is what keeps a double click from starting a second download.
    private void OnInstallClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel || RowOf(sender) is not { } row) return;
        var command = (System.Windows.Input.ICommand)viewModel.InstallCommand;
        if (command.CanExecute(row)) command.Execute(row);
    }

    private void OnOpenClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } viewModel && RowOf(sender) is { } row) viewModel.OpenCommand.Execute(row).Subscribe();
    }
}
