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

            PackageGrid.Thumbnails = viewModel.Icons;
            viewModel.WhenAnyValue(vm => vm.Packages)
                .Subscribe(packages => PackageGrid.Items = packages)
                .DisposeWith(disposables);
        });
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
