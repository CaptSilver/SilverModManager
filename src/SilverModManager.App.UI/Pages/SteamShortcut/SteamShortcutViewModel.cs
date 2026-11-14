using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using SilverModManager.App.UI.Extensions;
using SilverModManager.App.UI.Overlays;
using SilverModManager.App.UI.Windows;
using SilverModManager.Platform.Proton.Steam;
using SilverModManager.Platform.Proton.Steam.Shortcuts;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Games;

namespace SilverModManager.App.UI.Pages.SteamShortcut;

public partial class SteamShortcutViewModel : AOverlayViewModel<ISteamShortcutViewModel, Unit>, ISteamShortcutViewModel
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly ISteamShortcutService _service;
    private readonly IProcessTracker _tracker;
    private readonly SteamCloseAndWrite _closeAndWrite;
    private readonly IErrorPresenter _errors;
    private readonly bool _isGameMode;
    private CancellationTokenSource? _waitForSteam;
    private bool _isBusy;

    public SteamShortcutViewModel(ISteamShortcutService service, IProcessTracker tracker, SteamCloseAndWrite closeAndWrite, IErrorPresenter errors, SteamShortcutStatus status, bool isGameMode)
    {
        _service = service;
        _tracker = tracker;
        _closeAndWrite = closeAndWrite;
        _errors = errors;
        _isGameMode = isGameMode;

        IsRemoving = status.State is SteamShortcutState.InSteam or SteamShortcutState.AddedUnconfirmed;
        Title = IsRemoving ? "Remove from Steam" : "Add to Steam";
        Accounts = status.State == SteamShortcutState.AccountUndecided ? status.Candidates : [];
        SelectedAccount = Accounts.Count == 1 ? Accounts[0] : null;
        AskSteamNote = "Steam shows the entry under the launcher script's name, without an icon, until it is added again with Steam closed.";
        ApplySteamState(tracker.IsSteamRunning());

        var accountChosen = this.WhenAnyValue(vm => vm.SelectedAccount).Select(account => Accounts.Count == 0 || account is not null);
        AddCommand = ReactiveCommand.CreateFromTask(() => AddAsync("file"), this.WhenAnyValue(vm => vm.CanAdd).CombineLatest(accountChosen, (can, chosen) => can && chosen));
        CloseAndAddCommand = ReactiveCommand.CreateFromTask(CloseAndAddAsync, this.WhenAnyValue(vm => vm.CanCloseAndAdd).CombineLatest(accountChosen, (can, chosen) => can && chosen));
        AskSteamCommand = ReactiveCommand.CreateFromTask(() => AddAsync("steam-url"), this.WhenAnyValue(vm => vm.CanAskSteam).CombineLatest(accountChosen, (can, chosen) => can && chosen));
        RemoveCommand = ReactiveCommand.CreateFromTask(RemoveAsync, this.WhenAnyValue(vm => vm.CanRemove).CombineLatest(accountChosen, (can, chosen) => can && chosen));
        CloseAndRemoveCommand = ReactiveCommand.CreateFromTask(CloseAndRemoveAsync, this.WhenAnyValue(vm => vm.CanCloseAndRemove).CombineLatest(accountChosen, (can, chosen) => can && chosen));
        CancelWaitCommand = ReactiveCommand.Create(CancelWait, this.WhenAnyValue(vm => vm.IsWaitingForSteam));
        CloseCommand = ReactiveCommand.Create(() => Complete(Unit.Default));

        this.WhenActivated(d =>
        {
            Observable.Interval(PollInterval, TaskPoolScheduler.Default)
                .Select(_ => _tracker.IsSteamRunning())
                .DistinctUntilChanged()
                .OnUI()
                .Subscribe(ApplySteamState)
                .DisposeWith(d);
        });
    }

    public string Title { get; }
    public bool IsRemoving { get; }
    public IReadOnlyList<SteamAccount> Accounts { get; }
    public string AskSteamNote { get; }
    [Reactive] public partial SteamAccount? SelectedAccount { get; set; }
    [Reactive] public partial bool IsSteamRunning { get; private set; }
    [Reactive] public partial bool CanAdd { get; private set; }
    [Reactive] public partial bool CanCloseAndAdd { get; private set; }
    [Reactive] public partial bool CanAskSteam { get; private set; }
    [Reactive] public partial bool CanRemove { get; private set; }
    [Reactive] public partial bool CanCloseAndRemove { get; private set; }
    [Reactive] public partial bool IsWaitingForSteam { get; private set; }
    [Reactive] public partial string Note { get; private set; } = "";
    [Reactive] public partial string Message { get; private set; } = "";
    public ReactiveCommand<Unit, Unit> AddCommand { get; }
    public ReactiveCommand<Unit, Unit> CloseAndAddCommand { get; }
    public ReactiveCommand<Unit, Unit> AskSteamCommand { get; }
    public ReactiveCommand<Unit, Unit> RemoveCommand { get; }
    public ReactiveCommand<Unit, Unit> CloseAndRemoveCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelWaitCommand { get; }
    public ReactiveCommand<Unit, Unit> CloseCommand { get; }

    // Only the choice made in the picker is sent; with one account the service picks it itself.
    private uint? AccountId => Accounts.Count > 0 ? SelectedAccount?.AccountId : null;

    public void RefreshSteamState() => ApplySteamState(_tracker.IsSteamRunning());

    public async Task AddAsync(string route)
    {
        // The button may be a poll behind, so look again before touching Steam's files.
        RefreshSteamState();
        if (route == "file" && IsSteamRunning) { Message = Note; return; }

        await Run(() => Task.Run(() => _service.AddAsync(route, AccountId, CancellationToken.None)), "adding the app to Steam", retry: null,
            done: route == "file" ? "Added. Start Steam to see it." : "Asked Steam to add it. " + AskSteamNote);
    }

    public async Task RemoveAsync()
    {
        RefreshSteamState();
        if (IsSteamRunning) { Message = Note; return; }

        await Run(() => Task.Run(() => _service.RemoveAsync(AccountId, CancellationToken.None)), "removing the app from Steam", retry: null, done: "Removed from Steam.");
    }

    public Task CloseAndAddAsync() => CloseAndRun(token => _service.AddAsync("file", AccountId, token), "adding the app to Steam", "Added. Steam is starting again.");

    public Task CloseAndRemoveAsync() => CloseAndRun(token => _service.RemoveAsync(AccountId, token), "removing the app from Steam", "Removed. Steam is starting again.");

    /// <summary>
    /// Asks Steam to exit, runs the add or removal once it has, and starts Steam again.
    /// </summary>
    private async Task CloseAndRun(Func<CancellationToken, Task> write, string activity, string done)
    {
        if (_isBusy || _isGameMode) return;

        using var cts = new CancellationTokenSource();
        _waitForSteam = cts;
        SetBusy(true);
        try
        {
            var outcome = await _closeAndWrite.RunAsync(
                async () =>
                {
                    // Steam is gone and the file is about to change, so Cancel has nothing left to stop.
                    IsWaitingForSteam = false;
                    Message = "Writing…";
                    await Task.Run(() => write(cts.Token));
                    return true;
                },
                restartSteam: _ => true,
                cts.Token,
                onWaiting: () =>
                {
                    IsWaitingForSteam = true;
                    Message = "Waiting for Steam to close…";
                });

            Message = outcome.Outcome switch
            {
                SteamCloseOutcome.TimedOut => "Steam didn't close in time, so nothing was changed. Check Steam for a prompt that keeps it open, or close it completely (including the tray icon), then try again.",
                SteamCloseOutcome.Cancelled => "Cancelled, nothing was changed. Steam may still be closing.",
                _ => done,
            };
        }
        catch (Exception ex)
        {
            Message = ex.Message;
            await Report(ex, activity, () => CloseAndRun(write, activity, done));
        }
        finally
        {
            _waitForSteam = null;
            IsWaitingForSteam = false;
            SetBusy(false);
        }
    }

    private async Task Run(Func<Task> action, string activity, Func<Task>? retry, string done)
    {
        if (_isBusy) return;
        SetBusy(true);
        try
        {
            await action();
            Message = done;
        }
        catch (Exception ex)
        {
            Message = ex.Message;
            await Report(ex, activity, retry ?? Retry);
        }
        finally { SetBusy(false); }
    }

    // A refusal because Steam runs offers the dialog's own close-write-restart flow as the way past it.
    private Task Retry() => IsRemoving ? CloseAndRemoveAsync() : CloseAndAddAsync();

    private async Task Report(Exception ex, string activity, Func<Task> retry)
    {
        if (ex is SteamShortcutRefusedException { SteamRunning: true } refusal && !_isGameMode) refusal.Action = new CloseSteamAndRetry(retry);
        await _errors.Report(ex, activity);
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

    private void ApplySteamState(bool running)
    {
        IsSteamRunning = running;
        var idle = !_isBusy;
        CanAdd = !IsRemoving && !running && idle;
        CanCloseAndAdd = !IsRemoving && running && !_isGameMode && idle;
        // Asking Steam is the only route Game Mode has, and on the desktop it is the one that leaves Steam open.
        CanAskSteam = !IsRemoving && running && idle;
        CanRemove = IsRemoving && !running && !_isGameMode && idle;
        CanCloseAndRemove = IsRemoving && running && !_isGameMode && idle;
        Note = (IsRemoving, running, _isGameMode) switch
        {
            (true, _, true) => "Remove the shortcut from Desktop Mode. Steam is running this app, so it cannot be edited from here.",
            (false, true, true) => "Steam is running this app, so it cannot be closed from here. Asking Steam is the one way to add the shortcut.",
            (_, true, false) => "Steam rewrites its shortcuts when it exits, so a change made while it runs is lost. Close it completely, including the tray icon, or let this close and restart it.",
            _ => "",
        };
    }
}
