using System.Reactive.Disposables;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.ReactiveUI;
using JetBrains.Annotations;
using ReactiveUI;

namespace SilverModManager.App.UI.Pages.Thunderstore;

[UsedImplicitly]
public partial class ThunderstoreView : ReactiveUserControl<IThunderstoreViewModel>
{
    public ThunderstoreView()
    {
        InitializeComponent();

        this.WhenActivated(disposables =>
        {
            if (ViewModel is not { } viewModel) return;

            this.BindCommand(ViewModel, vm => vm.RefreshCommand, view => view.RefreshButton).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.SearchText, view => view.SearchBox.Text).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.ShowDeprecated, view => view.DeprecatedBox.IsChecked).DisposeWith(disposables);

            viewModel.WhenAnyValue(vm => vm.ErrorText)
                .Subscribe(text =>
                {
                    ErrorText.Text = text;
                    ErrorText.IsVisible = text != "";
                })
                .DisposeWith(disposables);

            // The template builds a line only when the list realizes it, so the whole catalog can be chunked as is.
            viewModel.WhenAnyValue(vm => vm.Packages)
                .Subscribe(packages =>
                {
                    _packages = packages;
                    Rebuild();
                })
                .DisposeWith(disposables);
        });
    }

    private IReadOnlyList<ThunderstorePackageRow> _packages = [];
    private int _columns;

    private void OnListSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (ThunderstoreCardLayout.ColumnsFor(ListWidth()) != _columns) Rebuild();
    }

    // The scrollbar takes its share of the list's width.
    private double ListWidth() => PackageList.Bounds.Width - 24;

    private void Rebuild()
    {
        _columns = ThunderstoreCardLayout.ColumnsFor(ListWidth());
        PackageList.ItemsSource = ThunderstoreCardLayout.Chunk(_packages, _columns);
    }

    private static ThunderstorePackageRow? RowOf(object? sender) => (sender as Control)?.DataContext as ThunderstorePackageRow;

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
        if (ViewModel is { } viewModel && RowOf(sender) is { } row) viewModel.OpenPackageCommand.Execute(row).Subscribe();
    }
}
