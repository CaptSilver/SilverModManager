using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
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

        GameFileRows.ItemTemplate = new FuncDataTemplate<WabbajackGameFileChoice>((file, _) =>
        {
            if (file is null) return null;
            // The box is recycled with its row, so the choice it writes to is the one it currently shows.
            var box = new CheckBox { Content = file.Path.ToString(), IsChecked = file.Keep };
            box.IsCheckedChanged += (_, _) => file.Keep = box.IsChecked == true;
            return box;
        });
        SkippedRows.ItemTemplate = new FuncDataTemplate<WabbajackSkippedRow>((row, _) =>
            row is null ? null : new TextBlock { Text = $"{row.To}: {row.Reason}", TextWrapping = TextWrapping.Wrap });

        this.WhenActivated(disposables =>
        {
            if (ViewModel is not { } viewModel) return;

            // The view model changes from download and job threads.
            IObservable<T> OnUi<T>(Func<IWabbajackModlistViewModel, IObservable<T>> select) => select(viewModel).ObserveOn(RxApp.MainThreadScheduler);

            this.BindCommand(ViewModel, vm => vm.DownloadCommand, view => view.DownloadButton).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.OpenNextFiveCommand, view => view.OpenNextFiveButton).DisposeWith(disposables);
            MenuCheckInbox.Command = viewModel.CheckInboxCommand;
            MenuOpenInboxFolder.Command = viewModel.OpenInboxFolderCommand;
            MenuChooseDownloadsFolder.Command = viewModel.ChooseDownloadsFolderCommand;
            MenuDefaultDownloadsFolder.Command = viewModel.UseDefaultDownloadsFolderCommand;
            MenuOpenReadme.Command = viewModel.OpenReadmeCommand;
            MenuRemove.Command = viewModel.RemoveCommand;
            this.BindCommand(ViewModel, vm => vm.InstallCommand, view => view.InstallButton).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.InstallCommand, view => view.ResumeButton).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.OpenHealthCheckCommand, view => view.HealthCheckButton).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.SkipMissingArchives, view => view.SkipBox.IsChecked).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.PickUpFromDownloads, view => view.PickUpBox.IsChecked).DisposeWith(disposables);
            OnUi(vm => vm.WhenAnyValue(x => x.HasWaitingArchives)).Subscribe(waiting => PickUpBox.IsVisible = waiting).DisposeWith(disposables);
            OnUi(vm => vm.WhenAnyValue(x => x.PickUpTooltip)).Subscribe(text => ToolTip.SetTip(PickUpBox, text)).DisposeWith(disposables);

            OnUi(vm => vm.WhenAnyValue(x => x.Name)).Subscribe(text => CollectionName.Text = text).DisposeWith(disposables);
            OnUi(vm => vm.WhenAnyValue(x => x.Author)).Subscribe(text => AuthorName.Text = text).DisposeWith(disposables);
            OnUi(vm => vm.WhenAnyValue(x => x.GameName)).Subscribe(text => GameText.Text = text).DisposeWith(disposables);
            OnUi(vm => vm.WhenAnyValue(x => x.Version)).Subscribe(text => VersionText.Text = $"Version {text}").DisposeWith(disposables);
            OnUi(vm => vm.WhenAnyValue(x => x.ArchiveCountText)).Subscribe(text => ArchiveCount.Text = text).DisposeWith(disposables);
            OnUi(vm => vm.WhenAnyValue(x => x.ArchiveSizeText)).Subscribe(text => ArchiveSize.Text = text).DisposeWith(disposables);
            OnUi(vm => vm.WhenAnyValue(x => x.Banner)).Subscribe(bitmap =>
            {
                CollectionImage.Source = bitmap;
                // A list's image is optional; without one the title should start at the gutter, not after a blank slot.
                CollectionImageBorder.IsVisible = bitmap is not null;
                HeaderImageBorder.Background = bitmap is null ? null : new ImageBrush { Source = bitmap, Stretch = Stretch.UniformToFill, AlignmentY = AlignmentY.Top };
            }).DisposeWith(disposables);
            OnUi(vm => vm.WhenAnyValue(x => x.Description)).Subscribe(text =>
            {
                DescriptionText.Text = text;
                DescriptionPanel.IsVisible = text != "";
                UpdateDescriptionToggle();
            }).DisposeWith(disposables);
            // Whether the text gets cut off depends on the width it wraps at, so it is decided from the laid-out text.
            DescriptionText.LayoutUpdated += OnDescriptionLayoutUpdated;
            Disposable.Create(() => DescriptionText.LayoutUpdated -= OnDescriptionLayoutUpdated).DisposeWith(disposables);
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

            // The lines are built for the rows in view only, and a row that changes tells its own line; the list
            // itself is replaced only when the page shows other archives.
            ArchiveRows.ItemTemplate = new FuncDataTemplate<WabbajackArchiveRow>((row, _) => row is null ? null : new WabbajackArchiveLine(viewModel, row));
            OnUi(vm => vm.WhenAnyValue(x => x.VisibleArchives)).Subscribe(rows => ArchiveRows.ItemsSource = rows).DisposeWith(disposables);

            // The section holds the table's own rows, so its lines follow them the same way.
            NeedsYouRows.ItemTemplate = new FuncDataTemplate<WabbajackArchiveRow>((row, _) => row is null ? null : new WabbajackArchiveLine(viewModel, row));
            OnUi(vm => vm.WhenAnyValue(x => x.NeedsYou)).Subscribe(rows => NeedsYouRows.ItemsSource = rows).DisposeWith(disposables);
            OnUi(vm => vm.WhenAnyValue(x => x.HasNeedsYou)).Subscribe(has => NeedsYouPanel.IsVisible = has).DisposeWith(disposables);
            OnUi(vm => vm.WhenAnyValue(x => x.NeedsYouHeading)).Subscribe(text => NeedsYouHeading.Text = text).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.CopyNeedsYouCommand, view => view.CopyListButton).DisposeWith(disposables);
            OnUi(vm => vm.NeedsYouRevealRequested).Subscribe(_ => NeedsYouPanel.BringIntoView()).DisposeWith(disposables);

            // The combo box lists the filters in the order of the enum.
            OnUi(vm => vm.WhenAnyValue(x => x.ArchiveFilter)).Subscribe(filter => ArchiveFilterBox.SelectedIndex = (int)filter).DisposeWith(disposables);
            ArchiveFilterBox.SelectionChanged += (_, _) =>
            {
                if (ArchiveFilterBox.SelectedIndex >= 0) viewModel.ArchiveFilter = (WabbajackArchiveFilter)ArchiveFilterBox.SelectedIndex;
            };

            // Only beside a plain sentence about what is missing; a running check, download or error has its own line.
            this.BindCommand(ViewModel, vm => vm.ShowMissingCommand, view => view.ShowThemButton).DisposeWith(disposables);
            OnUi(vm => vm.WhenAnyValue(x => x.CanShowMissing, x => x.Status)).Subscribe(state =>
                ShowThemButton.IsVisible = state.Item1 && state.Item2 is { Text: not "", IsWarning: false, IsIndeterminate: false, Fraction: null }).DisposeWith(disposables);

            OnUi(vm => vm.WhenAnyValue(x => x.GameFilesReplaced)).Subscribe(files =>
            {
                GameFilesPanel.IsVisible = files.Count > 0;
                GameFileRows.ItemsSource = files;
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
                SkippedRows.ItemsSource = skipped;
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

    private void OnDescriptionLayoutUpdated(object? sender, EventArgs e) => UpdateDescriptionToggle();

    // The lines the text takes at the width it is laid out in, however many the TextBlock is currently allowed to show.
    private int WrappedLineCount()
    {
        var width = DescriptionText.Bounds.Width;
        if (width <= 0 || string.IsNullOrEmpty(DescriptionText.Text)) return 0;
        var typeface = new Typeface(DescriptionText.FontFamily, DescriptionText.FontStyle, DescriptionText.FontWeight, DescriptionText.FontStretch);
        var layout = new TextLayout(DescriptionText.Text, typeface, DescriptionText.FontSize, DescriptionText.Foreground,
            textWrapping: TextWrapping.Wrap, maxWidth: width, maxHeight: double.PositiveInfinity);
        return layout.TextLines.Count;
    }

    // "Show more" is only worth offering when the collapsed text is actually cut off; once expanded it stays so the
    // text can be collapsed again.
    private void UpdateDescriptionToggle()
    {
        var expanded = DescriptionText.MaxLines == 0;
        var cutOff = WrappedLineCount() > CollapsedLines;
        DescriptionToggle.IsVisible = DescriptionPanel.IsVisible && (expanded || cutOff);
    }

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
}
