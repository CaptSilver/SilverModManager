using Avalonia.Controls;
using Avalonia.ReactiveUI;
using JetBrains.Annotations;
using NexusMods.Games.FromSoft.Saves;
using ReactiveUI;
using R3;

namespace NexusMods.App.UI.Pages.FromSoftSaves;

[UsedImplicitly]
public partial class FromSoftSavesView : ReactiveUserControl<IFromSoftSavesViewModel>
{
    public FromSoftSavesView()
    {
        InitializeComponent();

        // Wired once here rather than on activation, so reopening the tab does not stack handlers.
        FolderBox.SelectionChanged += (_, _) =>
        {
            if (ViewModel is { } vm && FolderBox.SelectedIndex >= 0 && FolderBox.SelectedIndex < vm.Folders.Count)
                vm.SelectedFolder = vm.Folders[FolderBox.SelectedIndex];
        };
        FilesList.SelectionChanged += (_, _) =>
        {
            if (ViewModel is { } vm) vm.SelectedFile = FilesList.SelectedIndex >= 0 && FilesList.SelectedIndex < vm.Files.Count ? vm.Files[FilesList.SelectedIndex] : null;
        };
        BackupsList.SelectionChanged += (_, _) =>
        {
            if (ViewModel is { } vm) vm.SelectedBackup = BackupsList.SelectedIndex >= 0 && BackupsList.SelectedIndex < vm.Backups.Count ? vm.Backups[BackupsList.SelectedIndex] : null;
        };

        this.WhenActivated(disposables =>
        {
            if (ViewModel is not { } viewModel) return;

            this.BindCommand(ViewModel, vm => vm.BackupCommand, view => view.BackupButton).AddTo(disposables);
            this.BindCommand(ViewModel, vm => vm.QuarantineCommand, view => view.QuarantineButton).AddTo(disposables);
            this.BindCommand(ViewModel, vm => vm.RefreshCommand, view => view.RefreshButton).AddTo(disposables);
            this.BindCommand(ViewModel, vm => vm.RestoreCommand, view => view.RestoreButton).AddTo(disposables);
            this.BindCommand(ViewModel, vm => vm.ConfirmRestoreCommand, view => view.ConfirmRestoreButton).AddTo(disposables);
            this.BindCommand(ViewModel, vm => vm.CancelRestoreCommand, view => view.CancelRestoreButton).AddTo(disposables);
            this.BindCommand(ViewModel, vm => vm.ResolveCommand, view => view.ResolveButton).AddTo(disposables);

            // The lists are short and replaced as a whole, so rows are strings and selection is by index.
            viewModel.WhenAnyValue(vm => vm.Folders)
                .Subscribe(folders => FolderBox.ItemsSource = folders.Select(f => $"Steam account {f.SteamId}").ToArray())
                .AddTo(disposables);
            viewModel.WhenAnyValue(vm => vm.SelectedFolder, vm => vm.Folders,
                    (selected, folders) => selected is null ? -1 : folders.ToList().FindIndex(f => f.Folder == selected.Folder))
                .Subscribe(index => FolderBox.SelectedIndex = index)
                .AddTo(disposables);

            viewModel.WhenAnyValue(vm => vm.Files)
                .Subscribe(files => FilesList.ItemsSource = files.Select(Describe).ToArray())
                .AddTo(disposables);

            viewModel.WhenAnyValue(vm => vm.Backups)
                .Subscribe(backups => BackupsList.ItemsSource = backups.Select(Describe).ToArray())
                .AddTo(disposables);

            viewModel.WhenAnyValue(vm => vm.Problem).Subscribe(text => Show(ProblemText, text)).AddTo(disposables);
            viewModel.WhenAnyValue(vm => vm.Message).Subscribe(text => Show(MessageText, text)).AddTo(disposables);
            viewModel.WhenAnyValue(vm => vm.QuarantineDisabledReason).Subscribe(text => Show(QuarantineReasonText, text)).AddTo(disposables);
            viewModel.WhenAnyValue(vm => vm.RestorePrompt)
                .Subscribe(text =>
                {
                    RestorePromptText.Text = text;
                    RestorePanel.IsVisible = text != "";
                })
                .AddTo(disposables);
            viewModel.WhenAnyValue(vm => vm.TaintedMessage, vm => vm.HasTaintedSave)
                .Subscribe(state =>
                {
                    TaintedText.Text = state.Item1;
                    TaintedPanel.IsVisible = state.Item2;
                })
                .AddTo(disposables);
            viewModel.WhenAnyValue(vm => vm.Notes)
                .Subscribe(notes =>
                {
                    NotesPanel.Children.Clear();
                    foreach (var note in notes) NotesPanel.Children.Add(new TextBlock { Text = note, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
                })
                .AddTo(disposables);
        });
    }

    private static void Show(TextBlock block, string? text)
    {
        block.Text = text;
        block.IsVisible = !string.IsNullOrEmpty(text);
    }

    private static string Describe(SaveFileEntry file)
    {
        var what = file.Kind switch
        {
            SaveKind.Vanilla => "vanilla",
            SaveKind.SeamlessCoop => "Seamless Co-op",
            SaveKind.Modded => "modded session",
            _ => "not a file this page acts on",
        };
        return $"{file.Name}  ({what}{(file.IsBackup ? ", game's backup" : "")}, {file.Size:N0} bytes, {file.Modified.LocalDateTime:g})";
    }

    private static string Describe(SaveBackup backup)
    {
        var where = backup.Location switch
        {
            SaveBackupLocation.Quarantined => "quarantined",
            SaveBackupLocation.QuarantineBackup => "copy before quarantine",
            _ => "backup",
        };
        return $"{backup.SaveName}  {backup.Stamp}{(backup.Label == "" ? "" : "  " + backup.Label)}  ({where})";
    }
}
