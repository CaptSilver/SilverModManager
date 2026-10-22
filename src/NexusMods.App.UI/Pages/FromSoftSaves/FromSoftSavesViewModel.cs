using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Loadouts;
using NexusMods.App.UI.Extensions;
using NexusMods.App.UI.Windows;
using NexusMods.App.UI.WorkspaceSystem;
using NexusMods.Games.FromSoft;
using NexusMods.Games.FromSoft.Saves;
using FromSoftSavesService = NexusMods.Games.FromSoft.Saves.FromSoftSaves;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using NexusMods.UI.Sdk.Icons;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace NexusMods.App.UI.Pages.FromSoftSaves;

[UsedImplicitly]
public class FromSoftSavesViewModel : APageViewModel<IFromSoftSavesViewModel>, IFromSoftSavesViewModel
{
    private static readonly TimeSpan SteamPollInterval = TimeSpan.FromSeconds(2);

    private readonly IConnection _conn;
    private readonly FromSoftSavesService _saves;
    private readonly ILogger<FromSoftSavesViewModel> _logger;
    private SaveBackup? _pendingRestore;
    private SaveFolder? _pendingRestoreFolder;

    [Reactive] public LoadoutId LoadoutId { get; set; }
    [Reactive] public GameInstallation? Installation { get; set; }
    [Reactive] public string Problem { get; private set; } = "";
    [Reactive] public IReadOnlyList<SaveFolder> Folders { get; private set; } = [];
    [Reactive] public SaveFolder? SelectedFolder { get; set; }
    [Reactive] public IReadOnlyList<SaveFileEntry> Files { get; private set; } = [];
    [Reactive] public SaveFileEntry? SelectedFile { get; set; }
    [Reactive] public IReadOnlyList<SaveBackup> Backups { get; private set; } = [];
    [Reactive] public SaveBackup? SelectedBackup { get; set; }
    [Reactive] public bool IsSteamRunning { get; private set; }
    [Reactive] public bool CanQuarantine { get; private set; }
    [Reactive] public string QuarantineDisabledReason { get; private set; } = "";
    [Reactive] public string RestorePrompt { get; private set; } = "";
    [Reactive] public string Message { get; private set; } = "";
    [Reactive] public IReadOnlyList<string> Notes { get; private set; } = [];
    [Reactive] public bool HasTaintedSave { get; private set; }
    [Reactive] public string TaintedMessage { get; private set; } = "";

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
    public ReactiveCommand<Unit, Unit> BackupCommand { get; }
    public ReactiveCommand<Unit, Unit> RestoreCommand { get; }
    public ReactiveCommand<Unit, Unit> ConfirmRestoreCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelRestoreCommand { get; }
    public ReactiveCommand<Unit, Unit> QuarantineCommand { get; }
    public ReactiveCommand<Unit, Unit> ResolveCommand { get; }

    public FromSoftSavesViewModel(IWindowManager windowManager, IConnection conn, FromSoftSavesService saves, ILogger<FromSoftSavesViewModel> logger) : base(windowManager)
    {
        _conn = conn;
        _saves = saves;
        _logger = logger;

        TabIcon = IconValues.Save;
        TabTitle = "Saves";

        this.WhenAnyValue(vm => vm.LoadoutId).Subscribe(id =>
        {
            if (id != default) Installation = Loadout.Load(_conn.Db, id).InstallationInstance;
        });
        this.WhenAnyValue(vm => vm.Installation).Subscribe(_ => Refresh());
        this.WhenAnyValue(vm => vm.SelectedFolder).Subscribe(folder => Files = folder?.Files ?? []);
        // The installation is in here because the reasons name its game's vanilla save.
        this.WhenAnyValue(vm => vm.SelectedFile, vm => vm.IsSteamRunning, vm => vm.Installation).Subscribe(_ => ApplyQuarantineState());

        RefreshCommand = ReactiveCommand.Create(Refresh);
        BackupCommand = ReactiveCommand.Create(Backup, this.WhenAnyValue(vm => vm.SelectedFile, file => file is { IsKnown: true }));
        RestoreCommand = ReactiveCommand.Create(AskToRestore, this.WhenAnyValue(vm => vm.SelectedBackup, vm => vm.SelectedFolder, (backup, folder) => backup is not null && folder is not null));
        ConfirmRestoreCommand = ReactiveCommand.Create(ConfirmRestore, this.WhenAnyValue(vm => vm.RestorePrompt, prompt => prompt != ""));
        CancelRestoreCommand = ReactiveCommand.Create(CancelRestore, this.WhenAnyValue(vm => vm.RestorePrompt, prompt => prompt != ""));
        QuarantineCommand = ReactiveCommand.Create(Quarantine, this.WhenAnyValue(vm => vm.CanQuarantine));
        ResolveCommand = ReactiveCommand.Create(Resolve, this.WhenAnyValue(vm => vm.HasTaintedSave));

        // Steam can be closed with the page open, so keep the Quarantine button in step with the process table.
        this.WhenActivated(d =>
        {
            Observable.Interval(SteamPollInterval, TaskPoolScheduler.Default)
                .Select(_ => _saves.IsSteamRunning())
                .DistinctUntilChanged()
                .OnUI()
                .Subscribe(running => IsSteamRunning = running)
                .DisposeWith(d);
        });
    }

    private void Refresh()
    {
        CancelRestore();
        if (Installation is not { } installation)
        {
            Problem = "";
            Folders = [];
            Backups = [];
            SelectedFolder = null;
            SelectedFile = null;
            HasTaintedSave = false;
            return;
        }

        // Read before the rows change: the list views clear the selection as soon as their rows are replaced.
        var selectedFolder = SelectedFolder?.Folder;
        var selectedFile = SelectedFile?.Path;

        var listing = _saves.List(installation);
        Problem = listing.Problem;
        Folders = listing.Folders;

        // The same folder and file stay selected across a refresh, as far as they still exist.
        SelectedFolder = Folders.FirstOrDefault(f => f.Folder == selectedFolder) ?? Folders.FirstOrDefault();
        SelectedFile = SelectedFolder?.Files.FirstOrDefault(f => f.Path == selectedFile);
        SelectedBackup = null;

        try
        {
            Backups = _saves.BackupsOf(installation).List();
            var tainted = _saves.LedgerOf(installation).Entries.Where(e => e.Tainted).Select(e => e.Folder).ToArray();
            HasTaintedSave = tainted.Length > 0;
            TaintedMessage = HasTaintedSave
                ? $"A modded session changed the vanilla save in {string.Join(", ", tainted)} and it could not be put back. " +
                  "Check the save, then resolve this; until then the game cannot be started without mods."
                : "";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Could not read the saved copies of {Game}", installation.Game.DisplayName);
            Message = $"Could not read the saved copies: {exception.Message}";
        }

        IsSteamRunning = _saves.IsSteamRunning();
    }

    // Falls back to a generic name when no FromSoft installation is set, where the page lists nothing to act on anyway.
    private string VanillaSaveName => Installation?.Game is IFromSoftGame game ? game.SaveNames.Vanilla : "vanilla save file";

    private void ApplyQuarantineState()
    {
        QuarantineDisabledReason = SelectedFile switch
        {
            null => $"Select the vanilla save ({VanillaSaveName}) first.",
            { Kind: not SaveKind.Vanilla } or { IsBackup: true } => $"Only the vanilla save ({VanillaSaveName}) can be quarantined.",
            _ when IsSteamRunning => "Close Steam first, completely including its tray icon. It may sync the save back from Steam Cloud.",
            _ => "",
        };
        CanQuarantine = QuarantineDisabledReason == "";
    }

    private void Backup()
    {
        if (SelectedFile is not { } file) return;
        Run("back up", (installation, folder) =>
        {
            var backup = _saves.Backup(installation, folder, file);
            return ($"Backed up {file.Name} to {backup.Path}.", []);
        });
    }

    private void AskToRestore()
    {
        if (SelectedBackup is not { } backup || SelectedFolder is not { } folder) return;
        (_pendingRestore, _pendingRestoreFolder) = (backup, folder);
        RestorePrompt = $"Restore {Path.GetFileName(backup.Path)} over {backup.SaveName} in {folder.SteamId}? " +
                        $"What is there now is copied to the backups folder first, labelled pre-restore.";
    }

    private void ConfirmRestore()
    {
        var (backup, folder) = (_pendingRestore, _pendingRestoreFolder);
        CancelRestore();
        if (backup is null || folder is null) return;

        Run("restore", (installation, _) =>
        {
            var result = _saves.Restore(installation, folder, backup);
            var kept = result.PreRestoreBackup is { } pre ? $" The save it replaced is kept as {pre.Path}." : "";
            return ($"Restored {Path.GetFileName(backup.Path)} to {result.Destination}.{kept}", []);
        });
    }

    private void CancelRestore()
    {
        (_pendingRestore, _pendingRestoreFolder) = (null, null);
        RestorePrompt = "";
    }

    private void Quarantine()
    {
        if (SelectedFile is not { } file) return;

        // The button may be a poll behind, so the service looks at Steam again before it moves anything.
        Run("quarantine", (installation, folder) =>
        {
            var result = _saves.Quarantine(installation, folder, file);
            return (result.Notes[0], result.Notes.Skip(1).ToArray());
        });
    }

    private void Resolve()
    {
        if (Installation is not { } installation) return;
        _saves.LedgerOf(installation).Resolve();
        Message = "Resolved. The kept copies were dropped.";
        Notes = [];
        Refresh();
    }

    // Refusals and file errors are what the user needs to read; anything else is a bug and is left to surface.
    private void Run(string verb, Func<GameInstallation, SaveFolder, (string Message, IReadOnlyList<string> Notes)> operation)
    {
        if (Installation is not { } installation || SelectedFolder is not { } selectedFolder) return;

        try
        {
            (Message, Notes) = operation(installation, selectedFolder);
        }
        catch (Exception exception) when (exception is SaveRefusedException or IOException or UnauthorizedAccessException)
        {
            if (exception is not SaveRefusedException) _logger.LogWarning(exception, "Could not {Verb} a save", verb);
            (Message, Notes) = (exception is SaveRefusedException ? exception.Message : $"Could not {verb}: {exception.Message}", []);
        }

        Refresh();
    }
}
