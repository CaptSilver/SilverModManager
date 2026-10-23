using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using SilverModManager.App.UI.Extensions;
using SilverModManager.App.UI.Overlays;
using SilverModManager.Platform.Proton.Launch;
using SilverModManager.Platform.Proton.Steam;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace SilverModManager.App.UI.Pages.SteamLaunchOption;

public class SteamLaunchOptionViewModel : AOverlayViewModel<ISteamLaunchOptionViewModel, Unit>, ISteamLaunchOptionViewModel
{
    private const string CloseSteamText = "Close Steam completely, including its tray icon, to write the launch option. " +
                                          "Steam rewrites its config when it exits and would discard the change.";

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly IProcessTracker _processTracker;
    private readonly ISteamLaunchOptionWriter _writer;
    private readonly IAvaloniaInterop _interop;
    private readonly SteamCloseAndWrite _closeAndWrite;
    private CancellationTokenSource? _waitForSteam;
    private bool _isBusy;
    private readonly Loadout.ReadOnly _loadout;
    private bool _replacementPending;

    public SteamLaunchOptionViewModel(Loadout.ReadOnly loadout, GameInstallation installation, IProcessTracker processTracker, ISteamLaunchOptionWriter writer, IAvaloniaInterop interop, SteamCloseAndWrite closeAndWrite)
    {
        _loadout = loadout;
        LoadoutName = loadout.Name;
        Installation = installation;
        _processTracker = processTracker;
        _writer = writer;
        _interop = interop;
        _closeAndWrite = closeAndWrite;

        Line = writer.ComposeLine(loadout);
        Warning = writer.MissingShimWarning ?? "";
        ApplySteamState(processTracker.IsSteamRunning());

        CopyCommand = ReactiveCommand.CreateFromTask(CopyAsync);
        WriteCommand = ReactiveCommand.CreateFromTask(WriteAsync, this.WhenAnyValue(vm => vm.CanWrite));
        ReplaceCommand = ReactiveCommand.CreateFromTask(ReplaceAsync, this.WhenAnyValue(vm => vm.CanReplace));
        CloseAndWriteCommand = ReactiveCommand.CreateFromTask(CloseAndWriteAsync, this.WhenAnyValue(vm => vm.CanCloseAndWrite));
        CancelWaitCommand = ReactiveCommand.Create(CancelWait, this.WhenAnyValue(vm => vm.IsWaitingForSteam));
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
    [Reactive] public bool CanCloseAndWrite { get; private set; }
    [Reactive] public bool IsWaitingForSteam { get; private set; }
    [Reactive] public string WriteNote { get; private set; } = "";
    [Reactive] public string Message { get; private set; } = "";
    [Reactive] public string OtherManagerName { get; private set; } = "";
    [Reactive] public string OldLine { get; private set; } = "";
    [Reactive] public string NewLine { get; private set; } = "";
    [Reactive] public bool CanReplace { get; private set; }
    public ReactiveCommand<Unit, Unit> CopyCommand { get; }
    public ReactiveCommand<Unit, Unit> WriteCommand { get; }
    public ReactiveCommand<Unit, Unit> ReplaceCommand { get; }
    public ReactiveCommand<Unit, Unit> CloseAndWriteCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelWaitCommand { get; }
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
        try
        {
            var result = await Task.Run(() => _writer.Write(Installation, _loadout));
            Message = DescribeWrite(result);
        }
        catch (Exception ex)
        {
            Message = DescribeFailure("write", ex);
        }
    }

    // The writer reports the failures it expects as refusals. Anything else would otherwise reach only the log.
    private static string DescribeFailure(string verb, Exception ex) => $"Could not {verb} the launch option: {ex.Message}";

    private string DescribeWrite(LocalConfigVdfWriter.Result result)
    {
        ShowReplacement(result.Replacement);
        return result.IsRefused
            ? result.Refusal!
            : $"Set the launch options of {Installation.Game.DisplayName} to: {result.Line} " +
              $"The previous file was kept as {result.Backup}. The line runs the current deploy of the loadout.";
    }

    /// <summary>
    /// Asks Steam to exit, writes the launch option once it has, and starts Steam again.
    /// </summary>
    public async Task CloseAndWriteAsync()
    {
        if (_isBusy) return;

        using var cts = new CancellationTokenSource();
        _waitForSteam = cts;
        SetBusy(true);
        try
        {
            var outcome = await _closeAndWrite.RunAsync(
                () =>
                {
                    // Steam is gone and the file is about to change, so Cancel has nothing left to stop.
                    IsWaitingForSteam = false;
                    Message = "Writing the launch option…";
                    return Task.Run(() => _writer.Write(Installation, _loadout));
                },
                // A line that needs Replace confirmed is written with Steam closed, so it must not come back first.
                restartSteam: result => !(result.IsRefused && result.Replacement is not null),
                cts.Token,
                onWaiting: () =>
                {
                    IsWaitingForSteam = true;
                    Message = "Waiting for Steam to close…";
                });

            Message = outcome.Outcome switch
            {
                SteamCloseOutcome.TimedOut => "Steam didn't close in time, so nothing was written. Check Steam for a prompt that keeps it open, or close it completely (including the tray icon), then try again.",
                SteamCloseOutcome.Cancelled => "Cancelled, nothing was written. Steam may still be closing.",
                _ => outcome.Value is { } written ? DescribeWrite(written) + DescribeRestart(written, outcome.SteamRestarted) : "",
            };
        }
        catch (Exception ex)
        {
            Message = DescribeFailure("write", ex);
        }
        finally
        {
            _waitForSteam = null;
            IsWaitingForSteam = false;
            SetBusy(false);
        }
    }

    private static string DescribeRestart(LocalConfigVdfWriter.Result result, bool restarted)
    {
        if (restarted) return " Steam is starting again.";
        return result.IsRefused && result.Replacement is not null
            ? " Steam was left closed so Replace can be written; start it yourself afterwards."
            : "";
    }

    public void CancelWait() => _waitForSteam?.Cancel();

    /// <summary>
    /// Stops a wait for Steam too, so nothing is written behind a dialog that is no longer there.
    /// </summary>
    public override void Close()
    {
        CancelWait();
        base.Close();
    }

    private void SetBusy(bool busy)
    {
        _isBusy = busy;
        RefreshSteamState();
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
        LocalConfigVdfWriter.Result result;
        try { result = await Task.Run(() => _writer.Replace(Installation, _loadout, shown)); }
        catch (Exception ex)
        {
            Message = DescribeFailure("replace", ex);
            return;
        }

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
        CanReplace = _replacementPending && !IsSteamRunning && !_isBusy;
    }

    private void ApplySteamState(bool running)
    {
        IsSteamRunning = running;
        // Closing Steam by hand mid-wait must not open a second write path next to the one already running.
        CanWrite = !running && !_isBusy;
        CanCloseAndWrite = running && !_isBusy;
        CanReplace = _replacementPending && !running && !_isBusy;
        WriteNote = running ? CloseSteamText : "";
    }
}
