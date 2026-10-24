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

            // The template builds a row only when the list realizes it, so the whole catalog can be bound as is.
            viewModel.WhenAnyValue(vm => vm.Packages)
                .Subscribe(packages => PackageList.ItemsSource = packages)
                .DisposeWith(disposables);
        });
    }

    private static ThunderstorePackageRow? RowOf(object? sender) => (sender as Control)?.DataContext as ThunderstorePackageRow;

    // Through ICommand, which honours CanExecute: the command is disabled while it runs, so a double click can't start a second install.
    private void OnInstallClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } viewModel && RowOf(sender) is { } row) ((System.Windows.Input.ICommand)viewModel.InstallCommand).Execute(row);
    }

    private void OnOpenClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } viewModel && RowOf(sender) is { } row) viewModel.OpenPackageCommand.Execute(row).Subscribe();
    }
}
