using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using NexusMods.App.UI.Extensions;
using NexusMods.App.UI.Overlays;
using NexusMods.Platform.Proton.Launch;
using NexusMods.Platform.Proton.Steam;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace NexusMods.App.UI.Pages.SteamLaunchOption;

public class SteamLaunchOptionViewModel : AOverlayViewModel<ISteamLaunchOptionViewModel, Unit>, ISteamLaunchOptionViewModel
{
    private const string CloseSteamText = "Close Steam completely, including its tray icon, to write the launch option. " +
                                          "Steam rewrites its config when it exits and would discard the change.";

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly IProcessTracker _processTracker;
    private readonly ISteamLaunchOptionWriter _writer;
    private readonly IAvaloniaInterop _interop;
    private readonly Loadout.ReadOnly _loadout;
    private bool _replacementPending;

    public SteamLaunchOptionViewModel(Loadout.ReadOnly loadout, GameInstallation installation, IProcessTracker processTracker, ISteamLaunchOptionWriter writer, IAvaloniaInterop interop)
    {
        _loadout = loadout;
        LoadoutName = loadout.Name;
        Installation = installation;
        _processTracker = processTracker;
        _writer = writer;
        _interop = interop;

        Line = writer.ComposeLine(loadout);
        Warning = writer.MissingShimWarning ?? "";
        ApplySteamState(processTracker.IsSteamRunning());

        CopyCommand = ReactiveCommand.CreateFromTask(CopyAsync);
        WriteCommand = ReactiveCommand.CreateFromTask(WriteAsync, this.WhenAnyValue(vm => vm.CanWrite));
        ReplaceCommand = ReactiveCommand.CreateFromTask(ReplaceAsync, this.WhenAnyValue(vm => vm.CanReplace));
        CloseCommand = ReactiveCommand.Create(() => Complete(Unit.Default));

        // The user may close Steam with the dialog open, so keep the Write button in step with the process table.
        this.WhenActivated(d =>
        {
            Observable.Interval(PollInterval, TaskPoolScheduler.Default)
                .Select(_ => _processTracker.IsSteamRunning())
                .DistinctUntilChanged()
                .OnUI()
                .Subscribe(ApplySteamState)
                .DisposeWith(d);
        });
    }

    public string LoadoutName { get; }
    public GameInstallation Installation { get; }
    public string Line { get; }
    public string Warning { get; }
    public string CopyNote => "Copying changes nothing in Steam. Paste the line into the game's Properties > Launch Options yourself.";
    [Reactive] public bool IsSteamRunning { get; private set; }
    [Reactive] public bool CanWrite { get; private set; }
    [Reactive] public string WriteNote { get; private set; } = "";
    [Reactive] public string Message { get; private set; } = "";
    [Reactive] public string OtherManagerName { get; private set; } = "";
    [Reactive] public string OldLine { get; private set; } = "";
    [Reactive] public string NewLine { get; private set; } = "";
    [Reactive] public bool CanReplace { get; private set; }
    public ReactiveCommand<Unit, Unit> CopyCommand { get; }
    public ReactiveCommand<Unit, Unit> WriteCommand { get; }
    public ReactiveCommand<Unit, Unit> ReplaceCommand { get; }
    public ReactiveCommand<Unit, Unit> CloseCommand { get; }

    public void RefreshSteamState() => ApplySteamState(_processTracker.IsSteamRunning());

    public async Task CopyAsync()
    {
        try
        {
            await _interop.SetClipboardTextAsync(Line);
            Message = "Copied. Nothing was changed in Steam.";
        }
        catch (InvalidOperationException ex)
        {
            Message = $"Could not copy to the clipboard: {ex.Message}";
        }
    }

    public Task WriteAsync()
    {
        // The button state may be a poll behind, so look again before touching Steam's files.
        RefreshSteamState();
        if (IsSteamRunning)
        {
            Message = CloseSteamText;
            return Task.CompletedTask;
        }

        return WriteToSteam();
    }

    private async Task WriteToSteam()
    {
        var result = await Task.Run(() => _writer.Write(Installation, _loadout));
        ShowReplacement(result.Replacement);
        Message = result.IsRefused
            ? result.Refusal!
            : $"Set the launch options of {Installation.Game.DisplayName} to: {result.Line} " +
              $"The previous file was kept as {result.Backup}. The line runs the current deploy of the loadout.";
    }

    public Task ReplaceAsync()
    {
        RefreshSteamState();
        if (IsSteamRunning)
        {
            Message = CloseSteamText;
            return Task.CompletedTask;
        }

        return ReplaceInSteam();
    }

    private async Task ReplaceInSteam()
    {
        // Send the line the user was shown, so a line changed underneath is refused instead of overwritten.
        var shown = OldLine;
        var result = await Task.Run(() => _writer.Replace(Installation, _loadout, shown));
        if (result.IsRefused)
        {
            // A fresh replacement means the line changed; the next press confirms the current one.
            if (result.Replacement is not null) ShowReplacement(result.Replacement);
            Message = result.Refusal!;
            return;
        }

        ShowReplacement(null);
        Message = $"Replaced the launch options of {Installation.Game.DisplayName}. " +
                  $"The old line is kept in {result.ReplacedLineRecord} and the previous file as {result.Backup}. " +
                  "The line runs the current deploy of the loadout.";
    }

    private void ShowReplacement(LocalConfigVdfWriter.Replacement? replacement)
    {
        _replacementPending = replacement is not null;
        OtherManagerName = replacement is null ? "" : replacement.ManagerName ?? "another launcher";
        OldLine = replacement?.OldLine ?? "";
        NewLine = replacement?.NewLine ?? "";
        CanReplace = _replacementPending && !IsSteamRunning;
    }

    private void ApplySteamState(bool running)
    {
        IsSteamRunning = running;
        CanWrite = !running;
        CanReplace = _replacementPending && !running;
        WriteNote = running ? CloseSteamText : "";
    }
}
