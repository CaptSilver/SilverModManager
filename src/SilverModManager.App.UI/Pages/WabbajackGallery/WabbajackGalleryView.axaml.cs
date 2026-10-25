using System.Reactive.Disposables;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.ReactiveUI;
using JetBrains.Annotations;
using ReactiveUI;
using SilverModManager.App.UI.Controls.Cards;

namespace SilverModManager.App.UI.Pages.WabbajackGallery;

[UsedImplicitly]
public partial class WabbajackGalleryView : ReactiveUserControl<IWabbajackGalleryViewModel>
{
    public WabbajackGalleryView()
    {
        InitializeComponent();

        this.WhenActivated(disposables =>
        {
            if (ViewModel is not { } viewModel) return;

            this.BindCommand(ViewModel, vm => vm.RefreshCommand, view => view.RefreshButton).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.SearchText, view => view.SearchBox.Text).DisposeWith(disposables);

            SortBox.Options = WabbajackGallerySorting.Choices;
            SortBox.BindSort(viewModel.WhenAnyValue(vm => vm.Sort), sort => viewModel.Sort = sort).DisposeWith(disposables);

            viewModel.WhenAnyValue(vm => vm.ErrorText)
                .Subscribe(text =>
                {
                    ErrorText.Text = text;
                    ErrorText.IsVisible = text != "";
                })
                .DisposeWith(disposables);

            viewModel.WhenAnyValue(vm => vm.RefreshedText)
                .Subscribe(text => RefreshedText.Text = text)
                .DisposeWith(disposables);

            ListGrid.Thumbnails = viewModel.Thumbnails;
            viewModel.WhenAnyValue(vm => vm.Lists)
                .Subscribe(lists => ListGrid.Items = lists)
                .DisposeWith(disposables);
        });
    }

    private static WabbajackGalleryRow? RowOf(object? sender) => (sender as Control)?.DataContext as WabbajackGalleryRow;

    // ReactiveCommand's ICommand.Execute doesn't look at CanExecute itself, and the command is disabled while it runs,
    // so asking first is what keeps a double click from starting a second download.
    private void OnInstallClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel || RowOf(sender) is not { } row) return;
        var command = (System.Windows.Input.ICommand)viewModel.InstallCommand;
        if (command.CanExecute(row)) command.Execute(row);
    }

    private void OnReadmeClick(object? sender, RoutedEventArgs e) => OpenLink(RowOf(sender)?.ReadmeUrl);

    private void OnWebsiteClick(object? sender, RoutedEventArgs e) => OpenLink(RowOf(sender)?.WebsiteUrl);

    private void OpenLink(Uri? url)
    {
        if (ViewModel is { } viewModel && url is not null) viewModel.OpenLinkCommand.Execute(url).Subscribe();
    }
}
