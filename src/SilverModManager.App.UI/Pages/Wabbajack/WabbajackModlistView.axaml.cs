using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.ReactiveUI;
using Avalonia.Threading;
using JetBrains.Annotations;
using ReactiveUI;

namespace SilverModManager.App.UI.Pages.Wabbajack;

[UsedImplicitly]
public partial class WabbajackModlistView : ReactiveUserControl<IWabbajackModlistViewModel>
{
    public WabbajackModlistView()
    {
        InitializeComponent();

        // The header and toolbar sit outside the scrolled part, and the page says "anywhere on this page".
        DragDrop.SetAllowDrop(Body, true);

        this.WhenActivated(disposables =>
        {
            if (ViewModel is not { } viewModel) return;

            // The view model changes from download and job threads.
            IObservable<T> OnUi<T>(Func<IWabbajackModlistViewModel, IObservable<T>> select) => select(viewModel).ObserveOn(RxApp.MainThreadScheduler);

            this.BindCommand(ViewModel, vm => vm.DownloadCommand, view => view.DownloadButton).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.OpenNextFiveCommand, view => view.OpenNextFiveButton).DisposeWith(disposables);
            MenuCheckInbox.Command = viewModel.CheckInboxCommand;
            MenuOpenInboxFolder.Command = viewModel.OpenInboxFolderCommand;
            MenuOpenReadme.Command = viewModel.OpenReadmeCommand;
            MenuRemove.Command = viewModel.RemoveCommand;
            this.BindCommand(ViewModel, vm => vm.InstallCommand, view => view.InstallButton).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.InstallCommand, view => view.ResumeButton).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.OpenHealthCheckCommand, view => view.HealthCheckButton).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.SkipMissingArchives, view => view.SkipBox.IsChecked).DisposeWith(disposables);

            OnUi(vm => vm.WhenAnyValue(x => x.Name)).Subscribe(text => CollectionName.Text = text).DisposeWith(disposables);
            OnUi(vm => vm.WhenAnyValue(x => x.Author)).Subscribe(text => AuthorName.Text = text).DisposeWith(disposables);
            OnUi(vm => vm.WhenAnyValue(x => x.GameName)).Subscribe(text => GameText.Text = text).DisposeWith(disposables);
            OnUi(vm => vm.WhenAnyValue(x => x.Version)).Subscribe(text => VersionText.Text = $"Version {text}").DisposeWith(disposables);
            OnUi(vm => vm.WhenAnyValue(x => x.ArchiveCountText)).Subscribe(text => ArchiveCount.Text = text).DisposeWith(disposables);
            OnUi(vm => vm.WhenAnyValue(x => x.ArchiveSizeText)).Subscribe(text => ArchiveSize.Text = text).DisposeWith(disposables);
            OnUi(vm => vm.WhenAnyValue(x => x.Banner)).Subscribe(bitmap =>
            {
                CollectionImage.Source = bitmap;
                HeaderImageBorder.Background = bitmap is null ? null : new ImageBrush { Source = bitmap, Stretch = Stretch.UniformToFill, AlignmentY = AlignmentY.Top };
            }).DisposeWith(disposables);
            OnUi(vm => vm.WhenAnyValue(x => x.Description)).Subscribe(text =>
            {
                DescriptionText.Text = text;
                DescriptionPanel.IsVisible = text != "";
                DescriptionToggle.IsVisible = WabbajackModlistText.IsLongDescription(text);
            }).DisposeWith(disposables);
            DescriptionToggle.Click += (_, _) =>
            {
                var expand = DescriptionText.MaxLines != 0;
                DescriptionText.MaxLines = expand ? 0 : CollapsedLines;
                DescriptionToggle.Text = expand ? "Show less" : "Show more";
            };
            OnUi(vm => vm.WhenAnyValue(x => x.ReadmeUri)).Subscribe(uri => MenuOpenReadme.IsVisible = uri is not null).DisposeWith(disposables);

            OnUi(vm => vm.WhenAnyValue(x => x.LoadoutChoices)).Subscribe(choices =>
            {
                LoadoutBox.ItemsSource = choices;
                LoadoutBox.SelectedItem = viewModel.SelectedLoadout;
            }).DisposeWith(disposables);
            OnUi(vm => vm.WhenAnyValue(x => x.SelectedLoadout)).Subscribe(choice =>
            {
                if (!Equals(LoadoutBox.SelectedItem, choice)) LoadoutBox.SelectedItem = choice;
            }).DisposeWith(disposables);
            LoadoutBox.SelectionChanged += (_, _) =>
            {
                if (LoadoutBox.SelectedItem is WabbajackLoadoutChoice choice && !Equals(choice, viewModel.SelectedLoadout))
                    viewModel.SelectLoadoutCommand.Execute(choice).Subscribe();
            };

            OnUi(vm => vm.WhenAnyValue(x => x.FreeSpaceText, x => x.IsFreeSpaceWarning)).Subscribe(state =>
            {
                FreeSpace.Text = state.Item1;
                FreeSpacePanel.IsVisible = state.Item1 != "";
                FreeSpacePanel.Classes.Set("Warning", state.Item2);
            }).DisposeWith(disposables);
            OnUi(vm => vm.WhenAnyValue(x => x.InstallBlockedReason)).Subscribe(text => ShowReason(text, InstallButton, ResumeButton)).DisposeWith(disposables);
            OnUi(vm => vm.WhenAnyValue(x => x.DownloadBlockedReason)).Subscribe(text => ShowReason(text, DownloadButton)).DisposeWith(disposables);
            OnUi(vm => vm.WhenAnyValue(x => x.OpenNextFiveBlockedReason)).Subscribe(text => ShowReason(text, OpenNextFiveButton)).DisposeWith(disposables);
            OnUi(vm => vm.WhenAnyValue(x => x.Status)).Subscribe(status =>
            {
                StatusText.Text = status.Text;
                StatusText.IsVisible = status.Text != "";
                StatusText.Classes.Set("Warning", status.IsWarning);
                StatusBar.IsVisible = status.Fraction is not null || status.IsIndeterminate;
                StatusBar.IsIndeterminate = status.IsIndeterminate;
                StatusBar.Value = status.Fraction ?? 0;
            }).DisposeWith(disposables);
            OnUi(vm => vm.WhenAnyValue(x => x.InboxText)).Subscribe(text =>
            {
                InboxText.Text = text;
                InboxText.IsVisible = text != "";
            }).DisposeWith(disposables);

            // Rows are rebuilt as a whole on every change; a list's archives are a few hundred lines at most.
            OnUi(vm => vm.WhenAnyValue(x => x.Archives)).Subscribe(rows =>
            {
                ArchiveRows.Children.Clear();
                foreach (var row in rows) ArchiveRows.Children.Add(ArchiveLine(viewModel, row));
            }).DisposeWith(disposables);

            OnUi(vm => vm.WhenAnyValue(x => x.GameFilesReplaced)).Subscribe(files =>
            {
                GameFilesPanel.IsVisible = files.Count > 0;
                GameFileRows.Children.Clear();
                foreach (var file in files)
                {
                    var box = new CheckBox { Content = file.Path.ToString(), IsChecked = file.Keep };
                    box.IsCheckedChanged += (_, _) => file.Keep = box.IsChecked == true;
                    GameFileRows.Children.Add(box);
                }
            }).DisposeWith(disposables);

            OnUi(vm => vm.WhenAnyValue(x => x.CanResume, x => x.IsInstalled, x => x.IsInstalling)).Subscribe(state =>
            {
                ResumeButton.IsVisible = state.Item1 && !state.Item3;
                InstallButton.IsVisible = !state.Item1;
                MenuRemove.IsVisible = state.Item2;
            }).DisposeWith(disposables);

            OnUi(vm => vm.WhenAnyValue(x => x.SkippedFiles)).Subscribe(skipped =>
            {
                SkippedPanel.IsVisible = skipped.Count > 0;
                SkippedRows.Children.Clear();
                foreach (var row in skipped)
                    SkippedRows.Children.Add(new TextBlock { Text = $"{row.To}: {row.Reason}", TextWrapping = TextWrapping.Wrap });
            }).DisposeWith(disposables);

            AddHandler(DragDrop.DragEnterEvent, OnDragOver);
            AddHandler(DragDrop.DragOverEvent, OnDragOver);
            AddHandler(DragDrop.DropEvent, OnDrop);
            Disposable.Create(() =>
            {
                RemoveHandler(DragDrop.DragEnterEvent, OnDragOver);
                RemoveHandler(DragDrop.DragOverEvent, OnDragOver);
                RemoveHandler(DragDrop.DropEvent, OnDrop);
            }).DisposeWith(disposables);
        });
    }

    private const int CollapsedLines = 3;

    // A disabled button says why in its tooltip (which a disabled control only shows when asked to). The page's
    // status line carries the one reason that matters, so the buttons don't each print their own.
    private static void ShowReason(string reason, params Control[] buttons)
    {
        foreach (var button in buttons)
        {
            ToolTip.SetShowOnDisabled(button, true);
            ToolTip.SetTip(button, reason == "" ? null : reason);
        }
    }

    // Handled, so the main window's own drop target (which adds to the library) leaves these to the page's inbox.
    // Enter too: moving onto the page raises DragEnter, not DragOver, and the window would show its overlay for it.
    private static void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (ViewModel is not { } viewModel) return;
        var paths = e.Data.GetFiles()?.Select(item => item.TryGetLocalPath()).OfType<string>().ToArray() ?? [];
        if (paths.Length == 0) return;
        _ = viewModel.AddDroppedFilesAsync(paths, CancellationToken.None);
    }

    private static Control ArchiveLine(IWabbajackModlistViewModel viewModel, WabbajackArchiveRow row)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Avalonia.Thickness(0, 2) };
        line.Children.Add(new TextBlock { Text = row.Name, Width = 320, TextTrimming = TextTrimming.CharacterEllipsis });
        line.Children.Add(new TextBlock { Text = row.SizeText, Width = 80 });
        line.Children.Add(new TextBlock { Text = row.Source, Width = 120 });
        line.Children.Add(new TextBlock { Text = row.StateText, Width = 200 });
        line.Children.Add(new TextBlock { Text = row.Reason, TextTrimming = TextTrimming.CharacterEllipsis, Opacity = 0.7 });
        if (row.CanInstall)
        {
            var install = new Button { Content = "Install" };
            install.Click += (_, _) => viewModel.InstallArchiveCommand.Execute(row).Subscribe();
            line.Children.Add(install);
        }
        if (row.CanOpenPage)
        {
            var open = new Button { Content = "Open page" };
            open.Click += (_, _) => viewModel.OpenPageCommand.Execute(row).Subscribe();
            line.Children.Add(open);
        }
        return line;
    }
}
