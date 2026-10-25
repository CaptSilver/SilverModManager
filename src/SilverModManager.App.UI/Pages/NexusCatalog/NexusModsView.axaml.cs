using System.Reactive.Disposables;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.ReactiveUI;
using JetBrains.Annotations;
using ReactiveUI;
using SilverModManager.Networking.NexusWebApi;

namespace SilverModManager.App.UI.Pages.NexusCatalog;

[UsedImplicitly]
public partial class NexusModsView : ReactiveUserControl<INexusModsViewModel>
{
    private static readonly NexusModsSort[] SortChoices = [NexusModsSort.Endorsements, NexusModsSort.Downloads, NexusModsSort.Updated, NexusModsSort.Name];

    public NexusModsView()
    {
        InitializeComponent();

        this.WhenActivated(disposables =>
        {
            if (ViewModel is not { } viewModel) return;

            this.BindCommand(ViewModel, vm => vm.LoadMoreCommand, view => view.LoadMoreButton).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.SearchText, view => view.SearchBox.Text).DisposeWith(disposables);

            SortBox.SelectedIndex = Array.IndexOf(SortChoices, viewModel.Sort);
            SortBox.SelectionChanged += OnSortChanged;
            Disposable.Create(() => SortBox.SelectionChanged -= OnSortChanged).DisposeWith(disposables);

            viewModel.WhenAnyValue(vm => vm.ErrorText)
                .Subscribe(text =>
                {
                    ErrorText.Text = text;
                    ErrorText.IsVisible = text != "";
                })
                .DisposeWith(disposables);

            viewModel.WhenAnyValue(vm => vm.HasMore)
                .Subscribe(hasMore => LoadMoreButton.IsVisible = hasMore)
                .DisposeWith(disposables);

            viewModel.WhenAnyValue(vm => vm.Mods)
                .Subscribe(mods => ModList.ItemsSource = mods)
                .DisposeWith(disposables);
        });
    }

    private void OnSortChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ViewModel is { } viewModel && SortBox.SelectedIndex is >= 0 and var index && index < SortChoices.Length)
            viewModel.Sort = SortChoices[index];
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
