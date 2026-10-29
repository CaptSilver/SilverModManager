using Microsoft.Extensions.DependencyInjection;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.App.UI.Overlays;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.Resources;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk;
using ReactiveUI;
using ReactiveUI.SourceGenerators;

namespace SilverModManager.App.UI.LeftMenu.Items;

public partial class LaunchButtonViewModel : AViewModel<ILaunchButtonViewModel>, ILaunchButtonViewModel
{
    [Reactive] public partial LoadoutId LoadoutId { get; set; } = Initializers.LoadoutId;

    [Reactive] public partial ReactiveCommand<Unit, Unit> Command { get; set; }

    public IObservable<bool> IsRunningObservable => _gameRunningTracker.GetWithCurrentStateAsStarting();

    [Reactive] public partial bool CanLaunch { get; set; } = true;

    [Reactive] public partial string Label { get; set; } = Language.LaunchButtonViewModel_LaunchGame_LAUNCH;

    [Reactive] public partial Percent? Progress { get; set; }

    [Reactive] public partial bool IsWaitingForSteam { get; private set; }

    public ReactiveCommand<Unit, Unit> CancelCommand { get; }

    // One launch at a time per button: the command cannot run again until the last one finished.
    private CancellationTokenSource? _launchCancellation;

    private readonly ILogger<ILaunchButtonViewModel> _logger;
    private readonly IToolManager _toolManager;
    private readonly IConnection _conn;
    private readonly IJobMonitor _monitor;
    private readonly IServiceProvider _serviceProvider;
    private readonly GameRunningTracker _gameRunningTracker;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// How long the wait for the game may take before the label hints that Steam or the game's launcher may be
    /// waiting on a window of its own (a licence, an update, a launcher's own chooser).
    /// </summary>
    public static readonly TimeSpan SlowStartHintDelay = TimeSpan.FromSeconds(10);

    // Guards the label state below: the tool reports from its own thread and the hint timer from another.
    private readonly object _labelLock = new();
    private string _waitingGameName = "";
    private bool _slowStart;
    private bool _installStep;
    private ITimer? _slowStartTimer;

    public LaunchButtonViewModel(ILogger<ILaunchButtonViewModel> logger, IToolManager toolManager, IConnection conn, IJobMonitor monitor, IServiceProvider serviceProvider, GameRunningTracker gameRunningTracker, TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
        _toolManager = toolManager;
        _conn = conn;
        _monitor = monitor;
        _serviceProvider = serviceProvider;
        _gameRunningTracker = gameRunningTracker;
        
        this.WhenActivated(cd =>
        {
            _gameRunningTracker.GetWithCurrentStateAsStarting().Subscribe(isRunning =>
            {
                if (isRunning)
                    SetLabelToRunning();
                else
                    SetLabelToLaunch();
            }).DisposeWith(cd);
        });

        Command = ReactiveCommand.CreateFromObservable(() => Observable.StartAsync(LaunchGame, RxApp.TaskpoolScheduler));
        // IsWaitingForSteam is set from the launch's thread pool thread, and the button reacts to CanExecuteChanged
        // on whichever thread raises it, so the command only hears about it on the UI thread.
        CancelCommand = ReactiveCommand.Create(CancelLaunch, this.WhenAnyValue(vm => vm.IsWaitingForSteam).OnUI());
    }

    private async Task LaunchGame(CancellationToken token)
    {
        var marker = Sdk.Loadouts.Loadout.Load(_conn.Db, LoadoutId);
        // The token from StartAsync is only cancelled when the subscription goes away, so Cancel needs its own.
        using var launchCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        _launchCancellation = launchCancellation;
        SetLabelToRunning();
        IDisposable? waitingSubscription = null;
        try
        {
            var gameName = marker.InstallationInstance.Game.DisplayName;
            var tool = _toolManager.GetTools(marker).OfType<IRunGameTool>().FirstOrDefault()
                ?? throw new NoRunGameToolException(gameName);
            waitingSubscription = new System.Reactive.Disposables.CompositeDisposable(
                tool.WaitingForSteam.Subscribe(waiting => SetWaitingForSteam(waiting, gameName)),
                tool.RunningInstallStep.Subscribe(running => SetRunningInstallStep(running, gameName)));
            var cancellationToken = launchCancellation.Token;
            await Task.Run(async () => await _toolManager.RunTool(tool, marker, _monitor, token: cancellationToken), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Launch was cancelled");
        }
        catch (Exception ex)
        {
            await _serviceProvider.GetRequiredService<IErrorPresenter>().Report(ex, "launching the game");
        }
        finally
        {
            waitingSubscription?.Dispose();
            StopSlowStartTimer();
            _launchCancellation = null;
        }
        IsWaitingForSteam = false;
        SetLabelToLaunch();
    }

    private void CancelLaunch()
    {
        try
        {
            _launchCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The launch finished between the click and here.
        }
    }

    private void SetWaitingForSteam(bool waiting, string gameName)
    {
        lock (_labelLock)
        {
            _waitingGameName = gameName;
            _slowStart = false;
            _installStep = false;
            StopSlowStartTimer();
            IsWaitingForSteam = waiting;
            if (waiting)
                _slowStartTimer = _timeProvider.CreateTimer(_ => MarkSlowStart(), null, SlowStartHintDelay, Timeout.InfiniteTimeSpan);
            Label = WaitingLabel(waiting);
        }
    }

    private void SetRunningInstallStep(bool running, string gameName)
    {
        lock (_labelLock)
        {
            _waitingGameName = gameName;
            _installStep = running;
            // The tool reports the install step only while it waits, so there is no label to touch otherwise.
            if (IsWaitingForSteam) Label = WaitingLabel(waiting: true);
        }
    }

    private void MarkSlowStart()
    {
        lock (_labelLock)
        {
            // The wait may have ended between the timer firing and getting here.
            if (!IsWaitingForSteam) return;
            _slowStart = true;
            Label = WaitingLabel(waiting: true);
        }
    }

    private void StopSlowStartTimer()
    {
        lock (_labelLock)
        {
            _slowStartTimer?.Dispose();
            _slowStartTimer = null;
        }
    }

    private string WaitingLabel(bool waiting)
    {
        if (!waiting) return Language.LaunchButtonViewModel_LaunchGame_RUNNING;
        if (_installStep) return string.Format(Language.LaunchButtonViewModel_LaunchGame_STEAM_INSTALL_STEP, _waitingGameName);
        return _slowStart
            ? Language.LaunchButtonViewModel_LaunchGame_WAITING_FOR_STEAM_SLOW
            : string.Format(Language.LaunchButtonViewModel_LaunchGame_WAITING_FOR_STEAM, _waitingGameName);
    }

    private void SetLabelToRunning() => Label = Language.LaunchButtonViewModel_LaunchGame_RUNNING;
    private void SetLabelToLaunch() => Label = Language.LaunchButtonViewModel_LaunchGame_LAUNCH;
}

/// <summary>Refuses a launch for a game the app has no run tool for, instead of failing with an empty sequence.</summary>
public sealed class NoRunGameToolException(string gameName)
    : UserFacingException($"The app has no way to start {gameName} yet, so the game was not launched.")
{
    public override string Title => "The game was not started";
}

