using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Exceptions;
using SilverModManager.App.UI.Overlays;
using SilverModManager.App.UI.Overlays.Generic.MessageBox.Ok;
using SilverModManager.App.UI.Resources;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace SilverModManager.App.UI.LeftMenu.Items;

public class LaunchButtonViewModel : AViewModel<ILaunchButtonViewModel>, ILaunchButtonViewModel
{
    [Reactive] public LoadoutId LoadoutId { get; set; } = Initializers.LoadoutId;

    [Reactive] public ReactiveCommand<Unit, Unit> Command { get; set; }

    public IObservable<bool> IsRunningObservable => _gameRunningTracker.GetWithCurrentStateAsStarting();

    [Reactive] public string Label { get; set; } = Language.LaunchButtonViewModel_LaunchGame_LAUNCH;

    [Reactive] public Percent? Progress { get; set; }

    [Reactive] public bool IsWaitingForSteam { get; private set; }

    public ReactiveCommand<Unit, Unit> CancelCommand { get; }

    // One launch at a time per button: the command cannot run again until the last one finished.
    private CancellationTokenSource? _launchCancellation;

    private readonly ILogger<ILaunchButtonViewModel> _logger;
    private readonly IToolManager _toolManager;
    private readonly IConnection _conn;
    private readonly IJobMonitor _monitor;
    private readonly IServiceProvider _serviceProvider;
    private readonly GameRunningTracker _gameRunningTracker;

    public LaunchButtonViewModel(ILogger<ILaunchButtonViewModel> logger, IToolManager toolManager, IConnection conn, IJobMonitor monitor, IServiceProvider serviceProvider, GameRunningTracker gameRunningTracker)
    {
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
            var tool = _toolManager.GetTools(marker).OfType<IRunGameTool>().First();
            var gameName = marker.InstallationInstance.Game.DisplayName;
            waitingSubscription = tool.WaitingForSteam.Subscribe(waiting => SetWaitingForSteam(waiting, gameName));
            var cancellationToken = launchCancellation.Token;
            await Task.Run(async () => await _toolManager.RunTool(tool, marker, _monitor, token: cancellationToken), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Launch was cancelled");
        }
        catch (ExecutableInUseException)
        {
            await MessageBoxOkViewModel.ShowGameAlreadyRunningError(_serviceProvider, marker.Installation.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error launching game");
        }
        finally
        {
            waitingSubscription?.Dispose();
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
        IsWaitingForSteam = waiting;
        Label = waiting
            ? string.Format(Language.LaunchButtonViewModel_LaunchGame_WAITING_FOR_STEAM, gameName)
            : Language.LaunchButtonViewModel_LaunchGame_RUNNING;
    }

    private void SetLabelToRunning() => Label = Language.LaunchButtonViewModel_LaunchGame_RUNNING;
    private void SetLabelToLaunch() => Label = Language.LaunchButtonViewModel_LaunchGame_LAUNCH;
}
