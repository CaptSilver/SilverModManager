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

        DragDrop.SetAllowDrop(DropZone, true);

        this.WhenActivated(disposables =>
        {
            if (ViewModel is not { } viewModel) return;

            // The view model changes from download and job threads.
            IObservable<T> OnUi<T>(Func<IWabbajackModlistViewModel, IObservable<T>> select) => select(viewModel).ObserveOn(RxApp.MainThreadScheduler);

            this.BindCommand(ViewModel, vm => vm.DownloadCommand, view => view.DownloadButton).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.OpenNextFiveCommand, view => view.OpenNextFiveButton).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.CheckInboxCommand, view => view.CheckInboxButton).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.OpenInboxFolderCommand, view => view.OpenInboxButton).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.InstallCommand, view => view.InstallButton).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.InstallCommand, view => view.ResumeButton).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.RemoveCommand, view => view.RemoveButton).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.OpenHealthCheckCommand, view => view.HealthCheckButton).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.SkipMissingArchives, view => view.SkipBox.IsChecked).DisposeWith(disposables);

            OnUi(vm => vm.WhenAnyValue(x => x.Name)).Subscribe(text => NameText.Text = text).DisposeWith(disposables);
            OnUi(vm => vm.WhenAnyValue(x => x.Author, x => x.Version, x => x.GameName, (author, version, game) => $"{author}, version {version}, for {game}"))
                .Subscribe(text => DetailsText.Text = text).DisposeWith(disposables);
            OnUi(vm => vm.WhenAnyValue(x => x.ArchiveSummary)).Subscribe(text => SummaryText.Text = text).DisposeWith(disposables);
            OnUi(vm => vm.WhenAnyValue(x => x.Banner)).Subscribe(bitmap =>
            {
                BannerImage.Source = bitmap;
                BannerImage.IsVisible = bitmap is not null;
            }).DisposeWith(disposables);
            OnUi(vm => vm.WhenAnyValue(x => x.ErrorText)).Subscribe(text =>
            {
                ErrorText.Text = text;
                ErrorText.IsVisible = text != "";
            }).DisposeWith(disposables);

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
                FreeSpaceText.Text = state.Item1;
                FreeSpaceText.Foreground = state.Item2 ? Brushes.OrangeRed : null;
            }).DisposeWith(disposables);
            OnUi(vm => vm.WhenAnyValue(x => x.AcquireText)).Subscribe(text => AcquireText.Text = text).DisposeWith(disposables);
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
                RemoveButton.IsVisible = state.Item2;
            }).DisposeWith(disposables);

            OnUi(vm => vm.WhenAnyValue(x => x.ProgressText)).Subscribe(text =>
            {
                ProgressText.Text = text;
                ProgressText.IsVisible = text != "";
                ProgressBar.IsVisible = text != "";
            }).DisposeWith(disposables);
            OnUi(vm => vm.WhenAnyValue(x => x.ProgressFraction)).Subscribe(fraction => ProgressBar.Value = fraction).DisposeWith(disposables);

            OnUi(vm => vm.WhenAnyValue(x => x.SkippedFiles)).Subscribe(skipped =>
            {
                SkippedPanel.IsVisible = skipped.Count > 0;
                SkippedRows.Children.Clear();
                foreach (var row in skipped)
                    SkippedRows.Children.Add(new TextBlock { Text = $"{row.To}: {row.Reason}", TextWrapping = TextWrapping.Wrap });
            }).DisposeWith(disposables);

            AddHandler(DragDrop.DragOverEvent, OnDragOver);
            AddHandler(DragDrop.DropEvent, OnDrop);
            Disposable.Create(() =>
            {
                RemoveHandler(DragDrop.DragOverEvent, OnDragOver);
                RemoveHandler(DragDrop.DropEvent, OnDrop);
            }).DisposeWith(disposables);
        });
    }

    private static void OnDragOver(object? sender, DragEventArgs e)
        => e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None;

    private void OnDrop(object? sender, DragEventArgs e)
    {
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
        if (row.CanOpenPage)
        {
            var open = new Button { Content = "Open page" };
            open.Click += (_, _) => viewModel.OpenPageCommand.Execute(row).Subscribe();
            line.Children.Add(open);
        }
        return line;
    }
}
