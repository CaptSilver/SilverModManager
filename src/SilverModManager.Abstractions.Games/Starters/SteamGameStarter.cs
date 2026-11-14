using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using BehaviorSubject = System.Reactive.Subjects.BehaviorSubject<bool>;

namespace SilverModManager.Abstractions.Games.Starters;

/// <summary>
/// Hands the game to Steam through <c>steam://run</c> and waits until Steam has started it and it is over.
/// One per run-game tool, since it carries that tool's Steam wait state for the launch button.
/// </summary>
public sealed class SteamGameStarter : IGameStarter
{
    private readonly ISteamUriLauncher _steamUris;
    private readonly IProcessTracker _processTracker;
    private readonly IRunningGameDetector _detector;
    private readonly ILogger _logger;

    // The timeout message needs the limit that ran out, which is known only inside Start.
    private readonly ConditionalWeakTable<GameStartRequest, StrongBox<TimeSpan>> _timeoutLimits = new();

    private readonly BehaviorSubject _waitingForSteam = new(false);
    private int _launchesWaitingForSteam;
    private readonly BehaviorSubject _runningInstallStep = new(false);

    /// <summary/>
    public SteamGameStarter(ISteamUriLauncher steamUris, IProcessTracker processTracker, IRunningGameDetector detector, ILogger logger)
    {
        _steamUris = steamUris;
        _processTracker = processTracker;
        _detector = detector;
        _logger = logger;
    }

    /// <inheritdoc cref="IRunGameTool.WaitingForSteam"/>
    public IObservable<bool> WaitingForSteam => _waitingForSteam;

    /// <inheritdoc cref="IRunGameTool.RunningInstallStep"/>
    public IObservable<bool> RunningInstallStep => _runningInstallStep;

    /// <inheritdoc />
    public GameStarterKind Kind => GameStarterKind.Steam;

    /// <inheritdoc />
    /// <remarks>The session records the arguments for the app's Steam launch option, which applies them.</remarks>
    public bool CanCarryArguments(GameStartRequest request) => true;

    /// <inheritdoc />
    public void CheckCanStart(GameStartRequest request) => AppIdOf(request);

    private static uint AppIdOf(GameStartRequest request)
    {
        var storeClient = request.Installation.LocatorResult.StoreClient;
        if (storeClient is not { Kind: StoreClientKind.Steam } || !uint.TryParse(storeClient.AppName, out var appId))
            throw new InvalidOperationException($"'{storeClient?.AppName}' is not a Steam app id");
        return appId;
    }

    /// <inheritdoc />
    public async Task<GameStartOutcome> Start(GameStartRequest request, IGameLaunch launch, CancellationToken token)
    {
        var appId = AppIdOf(request);

        // Never put arguments in this URL: Steam stops to ask the user to confirm them. The exe substitution and
        // extra arguments go into the launch session instead, and the app's Steam launch option applies them.
        // https://developer.valvesoftware.com/wiki/Steam_browser_protocol
        _steamUris.Open(new Uri($"steam://run/{appId}"));
        var wait = await WaitForSteamGame(launch, request, appId, token);
        if (wait.Outcome == GameStartOutcome.TimedOut) _timeoutLimits.AddOrUpdate(request, new StrongBox<TimeSpan>(wait.Limit));
        return wait.Outcome;
    }

    /// <inheritdoc />
    public string? AfterSessionMessage(GameStartRequest request, GameStartOutcome outcome)
    {
        if (outcome != GameStartOutcome.TimedOut || !_timeoutLimits.TryGetValue(request, out var limit)) return null;
        return $"Steam did not start {request.GameName} within {DescribeTimeout(limit.Value)}. Make sure Steam is running and signed in, then press Play again.";
    }

    /// <summary>How a wait for Steam ended, and for a timeout, the limit that ran out.</summary>
    private readonly record struct SteamWait(GameStartOutcome Outcome, TimeSpan Limit = default);

    /// <summary>
    /// The timeout as the user reads it: whole minutes when it is a whole number of them, seconds otherwise.
    /// </summary>
    internal static string DescribeTimeout(TimeSpan timeout)
    {
        var seconds = Math.Max(1, (int)Math.Ceiling(timeout.TotalSeconds));
        if (seconds % 60 == 0)
        {
            var minutes = seconds / 60;
            return minutes == 1 ? "1 minute" : $"{minutes} minutes";
        }
        return seconds == 1 ? "1 second" : $"{seconds} seconds";
    }

    private void SetRunningInstallStep(bool running)
    {
        lock (_runningInstallStep)
            if (_runningInstallStep.Value != running) _runningInstallStep.OnNext(running);
    }

    private void SetWaitingForSteam(int delta)
    {
        // Two launches can wait at once (two installations of one game), so this counts them instead of
        // flipping a flag the first to finish would clear for the other.
        lock (_waitingForSteam)
        {
            _launchesWaitingForSteam += delta;
            if (_waitingForSteam.Value != _launchesWaitingForSteam > 0) _waitingForSteam.OnNext(_launchesWaitingForSteam > 0);
        }
    }

    private async Task<SteamWait> WaitForSteamGame(IGameLaunch launch, GameStartRequest request, uint appId, CancellationToken cancellationToken)
    {
        SetWaitingForSteam(+1);
        var waiting = true;
        try
        {
            return await WaitForSteamGame(launch, request, appId, cancellationToken, () =>
            {
                if (!waiting) return;
                waiting = false;
                SetWaitingForSteam(-1);
            });
        }
        finally
        {
            if (waiting) SetWaitingForSteam(-1);
            SetRunningInstallStep(false);
        }
    }

    private async Task<SteamWait> WaitForSteamGame(IGameLaunch launch, GameStartRequest request, uint appId, CancellationToken cancellationToken, Action gameStarted)
    {
        var timing = request.Timing;
        if (await PollUntil(_processTracker.IsSteamRunning, timing.SteamStartTimeout, launch, timing, cancellationToken) is { } steamFailure) return steamFailure;

        // NOTE(erri120): Reaper is a custom tool for cleaning up child processes
        // See https://github.com/sonic2kk/steamtinkerlaunch/wiki/Steam-Reaper for details.
        // Only the reaper Steam started for this app counts: another game's reaper may be running already.
        if (await PollUntil(() => _processTracker.FindReaperForApp(appId) is not null, timing.StartTimeout, launch, timing, cancellationToken, appId) is { } reaperFailure)
            return reaperFailure;

        SetRunningInstallStep(false);
        gameStarted();
        // The reaper is only the first process of the game: Steam replaces it when the game relaunches, and the
        // game's own processes can outlive it. The game is over when the process table has shown nothing of it
        // for the settle period.
        _logger.LogDebug("Waiting for the processes of app {AppId} to exit", appId);
        try
        {
            await _detector.WaitUntilGone(request.Installation, new GoneWait(timing.SteamSettle, Interval: timing.PollInterval, Progress: OpenToolsProgress.Of(launch)), cancellationToken);
            return new SteamWait(GameStartOutcome.Exited);
        }
        catch (OperationCanceledException)
        {
            return new SteamWait(GameStartOutcome.Cancelled);
        }
    }

    /// <summary>
    /// Polls until <paramref name="condition"/> holds, returning null, or returns why it stopped. A timeout is
    /// not final while a Steam wrapper has recorded itself in the launch's state file (the game is on its way
    /// and the process table has just not caught up), or, when <paramref name="installStepOfApp"/> is given,
    /// while Steam is running that app's install step. Either one restarts the clock.
    /// </summary>
    private async Task<SteamWait?> PollUntil(Func<bool> condition, TimeSpan timeout, IGameLaunch launch, StartTiming timing, CancellationToken cancellationToken, uint? installStepOfApp = null)
    {
        var clock = timing.Clock;
        var started = clock.GetTimestamp();
        while (!cancellationToken.IsCancellationRequested)
        {
            if (condition()) return null;

            if (installStepOfApp is { } appId)
            {
                var installing = _processTracker.IsInstallStepRunning(appId);
                SetRunningInstallStep(installing);
                if (installing) started = clock.GetTimestamp();
            }

            if (clock.GetElapsedTime(started) >= timeout)
            {
                // Read the file afresh every time: the wrapper writes it from another process.
                if (!launch.WrapperActive)
                {
                    _logger.LogWarning("Steam did not start the game within `{Timeout:g}`", timeout);
                    return new SteamWait(GameStartOutcome.TimedOut, timeout);
                }
                started = clock.GetTimestamp();
            }

            try
            {
                await Task.Delay(timing.PollInterval, clock, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
        return new SteamWait(GameStartOutcome.Cancelled);
    }
}
