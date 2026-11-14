using System.Reactive.Disposables;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.ReactiveUI;
using JetBrains.Annotations;
using ReactiveUI;

namespace SilverModManager.App.UI.Pages.Catalog;

[UsedImplicitly]
public partial class CatalogView : ReactiveUserControl<ICatalogViewModel>
{
    public CatalogView()
    {
        InitializeComponent();

        this.WhenActivated(disposables =>
        {
            if (ViewModel is not { } viewModel) return;

            this.Bind(ViewModel, vm => vm.SearchText, view => view.SearchBox.Text).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.CatalogName, view => view.TitleText.Text).DisposeWith(disposables);

            SortBox.Options = viewModel.SortOptions;
            this.Bind(ViewModel, vm => vm.SortKey, view => view.SortBox.SelectedKey).DisposeWith(disposables);

            viewModel.WhenAnyValue(vm => vm.ErrorText)
                .Subscribe(text =>
                {
                    ErrorText.Text = text;
                    ErrorText.IsVisible = text != "";
                })
                .DisposeWith(disposables);

            viewModel.WhenAnyValue(vm => vm.StatusText)
                .Subscribe(text =>
                {
                    StatusText.Text = text;
                    StatusText.IsVisible = text != "";
                })
                .DisposeWith(disposables);

            CardList.Thumbnails = viewModel.Icons;
            viewModel.WhenAnyValue(vm => vm.Rows)
                .Subscribe(rows => CardList.Items = rows)
                .DisposeWith(disposables);
        });
    }

    // The grid reports the end on every scroll step near it, and again when a page leaves the view unfilled; the
    // command is disabled while a page loads, and the view model ignores the call once the catalog is exhausted.
    private void OnEndReached(object? sender, EventArgs e)
    {
        if (ViewModel is not { HasMore: true } viewModel) return;
        var command = (System.Windows.Input.ICommand)viewModel.LoadMoreCommand;
        if (command.CanExecute(null)) command.Execute(null);
    }

    private static CatalogCardRow? RowOf(object? sender) => (sender as Control)?.DataContext as CatalogCardRow;

    // ReactiveCommand's ICommand.Execute doesn't look at CanExecute itself, and the command is disabled while it runs,
    // so asking first is what keeps a double click from starting a second install.
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
