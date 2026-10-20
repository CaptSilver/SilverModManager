using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Games;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;

namespace NexusMods.Platform.Proton.Launch;

/// <summary>
/// How a launch ended.
/// </summary>
/// <param name="ExitCode">The game's exit code, or 125 when the launch itself failed.</param>
/// <param name="Message">Why the launch failed; null when it ran and the game's own exit code is the answer.</param>
public sealed record LaunchResult(int ExitCode, string? Message);

/// <summary>
/// The command a launch will start, with the steps that produced it, so the log can show how the final argv came
/// about.
/// </summary>
/// <param name="Process">What to start.</param>
/// <param name="Resolved">What the loadout's run tool resolved, or null when the command did not come from one.</param>
/// <param name="Substitution">How the launch command Steam handed over was rewritten, or null when there was none.</param>
public sealed record LaunchPlan(GameProcessSpec Process, LaunchSpec? Resolved = null, ExeSubstitution.Result? Substitution = null);

/// <summary>
/// One launch of one loadout: takes the installation lock, applies the loadout, runs the game and takes its
/// changes back. The lock is held, and <see cref="ActiveLaunchSessions.IsRunning"/> stays true, until
/// <see cref="Release"/>: the caller closes the database first, so nothing can write to a game folder that
/// another launch has already been let into.
/// </summary>
public sealed class LaunchSession
{
    /// <summary>The exit code for every failure of the launch itself, as opposed to the game's own.</summary>
    public const int FailureExitCode = 125;

    /// <summary>The exit code of a launch that was stopped by a signal and shut down cleanly (128 + SIGTERM).</summary>
    public const int StoppedExitCode = 143;

    private readonly IInstallationLockFactory _lockFactory;
    private readonly ActiveLaunchSessions _activeSessions;
    private readonly ILaunchSynchronizer _synchronizer;
    private readonly IGameProcessFactory _processFactory;
    private readonly IReadOnlyList<ILaunchGate> _gates;
    private readonly IDesktopNotifier _notifier;
    private readonly ILogger<LaunchSession> _logger;
    private readonly TextWriter _error;
    private readonly object _releaseLock = new();
    private readonly StopHandler? _stop;

    // Guards the phase, the stop flag and the process handle together: a signal must never see a phase that
    // disagrees with whether there is a child to terminate.
    private readonly object _stateGate = new();
    private LaunchPhase _phase = LaunchPhase.Preparing;
    private bool _stopRequested;
    private IGameProcess? _process;
    private Task? _terminating;

    private InstallationLock? _lock;
    private IDisposable? _registration;
    private bool _started;
    private bool _released;
    private bool _keepRunningState;
    private LaunchResult? _result;

    public LaunchSession(
        IInstallationLockFactory lockFactory,
        ActiveLaunchSessions activeSessions,
        ILaunchSynchronizer synchronizer,
        IGameProcessFactory processFactory,
        IEnumerable<ILaunchGate> gates,
        IDesktopNotifier notifier,
        ILogger<LaunchSession> logger,
        TextWriter? error = null,
        StopHandler? stop = null)
    {
        _lockFactory = lockFactory;
        _activeSessions = activeSessions;
        _synchronizer = synchronizer;
        _processFactory = processFactory;
        _gates = gates.ToList();
        _notifier = notifier;
        _logger = logger;
        _error = error ?? Console.Error;
        _stop = stop;
    }

    /// <summary>
    /// Runs the launch. Every failure comes back as exit code 125 with its message, which has already gone to
    /// stderr, the log and the desktop notifier; only a launch that ran returns the game's exit code.
    /// </summary>
    /// <param name="context">What is being launched.</param>
    /// <param name="resolveSpec">
    /// Builds the command once the loadout is applied, because what the run tool resolves (a script extender
    /// loader, say) can depend on files the apply just put in place.
    /// </param>
    public Task<LaunchResult> RunAsync(LaunchContext context, Func<GameProcessSpec> resolveSpec, CancellationToken cancellationToken = default)
        => RunAsync(context, () => new LaunchPlan(resolveSpec()), cancellationToken);

    /// <summary>
    /// Runs the launch like the overload taking a spec, and logs how <paramref name="resolvePlan"/>'s command came
    /// about before the game starts.
    /// </summary>
    /// <remarks>
    /// With a <see cref="StopHandler"/>, the first signal decides by phase. While preparing, the apply in progress
    /// is allowed to finish, the game is not started and the launch ends with <see cref="StoppedExitCode"/>. While
    /// the game runs it is terminated, the launch is finished once and ends with <see cref="StoppedExitCode"/>.
    /// While finishing the signal changes nothing. A game that survives SIGKILL is still
    /// <see cref="FailureExitCode"/> with no finish.
    /// </remarks>
    public Task<LaunchResult> RunAsync(LaunchContext context, Func<LaunchPlan> resolvePlan, CancellationToken cancellationToken = default)
        => Run(context, resolvePlan, whileRunning: null, cancellationToken);

    /// <summary>
    /// Runs the launch for a game that something else starts, such as Steam handing a URI to its client.
    /// <paramref name="whileRunning"/> is called with the launch marked running, and returns once the game is
    /// over or the caller has stopped waiting. What is left of the game is then shut down, so a game that
    /// outlives the wait is not ingested while it still writes.
    /// </summary>
    /// <remarks>
    /// A cancelled wait is a normal end, not a failure. Signals from a <see cref="StopHandler"/> only decide
    /// whether the wait starts: there is no child of ours to terminate, so the callback has to watch its own
    /// token.
    /// </remarks>
    public Task<LaunchResult> RunExternalAsync(LaunchContext context, Func<CancellationToken, Task> whileRunning, CancellationToken cancellationToken = default)
        => Run(context, resolvePlan: null, whileRunning, cancellationToken);

    private async Task<LaunchResult> Run(LaunchContext context, Func<LaunchPlan>? resolvePlan, Func<CancellationToken, Task>? whileRunning, CancellationToken cancellationToken)
    {
        if (_started) throw new InvalidOperationException("A launch session runs once.");
        _started = true;

        string? failure;
        if (_stop is null)
        {
            failure = await RunCore(context, resolvePlan, whileRunning, cancellationToken);
        }
        else
        {
            _stop.Stopping += OnStopping;
            try
            {
                // A signal that came in before we subscribed still counts.
                if (_stop.Signal is not null) OnStopping(_stop.Signal.Value, _stop.PhaseAtSignal ?? LaunchPhase.Preparing);
                failure = await RunCore(context, resolvePlan, whileRunning, cancellationToken);
            }
            finally
            {
                _stop.Stopping -= OnStopping;
            }
        }
        if (failure is null) return _result!;

        _logger.LogError("{Message}", failure);
        _error.WriteLine(failure);
        _notifier.Notify(ApplicationConstants.ProductName, failure);
        return new LaunchResult(FailureExitCode, failure);
    }

    /// <summary>
    /// Returns the failure message, or null with <see cref="_result"/> set when the game ran to the end.
    /// </summary>
    private async Task<string?> RunCore(LaunchContext context, Func<LaunchPlan>? resolvePlan, Func<CancellationToken, Task>? whileRunning, CancellationToken cancellationToken)
    {
        var game = context.Installation.Game.DisplayName;

        try
        {
            _lock = _lockFactory.Create(context.Installation.LocatorResult.Store.Value.ToLowerInvariant(), context.Installation.LocatorResult.StoreIdentifier);
            if (!_lock.TryAcquire()) return DescribeHolder(game, _lock);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException)
        {
            return $"Cannot start {game}: the launch lock is unavailable. {e.Message}";
        }

        _registration = _activeSessions.Register(context.Installation);

        foreach (var gate in _gates)
        {
            try
            {
                if (gate.Check(context) is { } veto) return veto.Message;
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Checking {Gate} for {Game} failed", gate.GetType().Name, game);
                return $"Cannot start {game}: checking whether it can be launched failed ({gate.GetType().Name}). {e.Message}";
            }
        }

        var prepared = new List<ILaunchGate>();
        try
        {
            _lock.WriteState(Environment.ProcessId, context.LoadoutName, LaunchPhase.Preparing);
            await _synchronizer.Synchronize(context.LoadoutId);
            foreach (var gate in _gates)
            {
                if (StopRequested) break;
                await gate.Prepare(context, cancellationToken);
                prepared.Add(gate);
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Preparing {Loadout} for {Game} failed", context.LoadoutName, game);
            return $"Could not prepare loadout '{context.LoadoutName}' for {game}: {e.Message}";
        }

        string? startFailure = null;
        GameProcessResult? exit = null;
        if (!StopRequested)
        {
            try
            {
                if (resolvePlan is not null)
                {
                    var plan = resolvePlan();
                    LogPlan(plan, game);
                    if (BeginRunning())
                    {
                        _lock.WriteState(Environment.ProcessId, context.LoadoutName, LaunchPhase.Running);
                        using var process = _processFactory.Start(plan.Process, context.Installation);
                        AttachProcess(process);
                        try
                        {
                            exit = await WaitFor(process, cancellationToken);
                        }
                        finally
                        {
                            // A terminate already under way must end before the handle is disposed.
                            if (DetachProcess() is { } terminating) await terminating;
                        }
                    }
                }
                else if (BeginRunning())
                {
                    _lock.WriteState(Environment.ProcessId, context.LoadoutName, LaunchPhase.Running);
                    try
                    {
                        await whileRunning!(cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        _logger.LogInformation("Stopped waiting for {Game}", game);
                    }
                    finally
                    {
                        // Whatever happened, the game must not be left running into the ingest.
                        var leftover = await _processFactory.ShutDownGameAsync(context.Installation, CancellationToken.None);
                        exit = new GameProcessResult(0, leftover);
                    }
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Running {Game} failed", game);
                startFailure = $"Could not start {game}: {e.Message}";
            }
        }
        else
        {
            _logger.LogInformation("Not starting {Game}: a stop signal arrived while preparing", game);
        }

        if (exit?.KillFailed is { } survivor)
        {
            // The game folder is in use; ingesting it or running the gates' teardown now could corrupt it.
            _keepRunningState = true;
            return $"{game} did not stop: process {survivor.Pid} ({survivor.Name}) survived SIGKILL. " +
                   "The loadout is still applied. Stop that process, then launch again.";
        }

        var stopped = BeginFinishing();
        var finishFailure = await Finish(context, prepared);

        if (startFailure is not null) return startFailure;
        if (finishFailure is not null) return finishFailure;

        _result = new LaunchResult(stopped ? StoppedExitCode : exit!.ExitCode, null);
        return null;
    }

    private bool StopRequested
    {
        get { lock (_stateGate) return _stopRequested; }
    }

    /// <summary>
    /// Moves to running unless a stop has been asked for first. Returns false when the game must not start.
    /// </summary>
    private bool BeginRunning()
    {
        lock (_stateGate)
        {
            if (_stopRequested) return false;
            SetPhase(LaunchPhase.Running);
            return true;
        }
    }

    /// <summary>
    /// Hands the child to the signal path. A stop that arrived between the phase change and the spawn is applied
    /// here, since there was no child to terminate when it came in.
    /// </summary>
    private void AttachProcess(IGameProcess process)
    {
        lock (_stateGate)
        {
            _process = process;
            if (_stopRequested) TerminateOnce();
        }
    }

    /// <summary>
    /// Takes the child away from the signal path once its wait is over, so a later signal can't start a terminate
    /// on a disposed handle. Returns the terminate a signal started, if any.
    /// </summary>
    private Task? DetachProcess()
    {
        lock (_stateGate)
        {
            _process = null;
            return _terminating;
        }
    }

    /// <summary>
    /// Moves to finishing and reports whether a signal had already decided how this launch ends.
    /// </summary>
    private bool BeginFinishing()
    {
        lock (_stateGate)
        {
            SetPhase(LaunchPhase.Finishing);
            return _stopRequested;
        }
    }

    private void SetPhase(LaunchPhase phase)
    {
        _phase = phase;
        if (_stop is not null) _stop.Phase = phase;
    }

    private void OnStopping(PosixSignal signal, LaunchPhase signalPhase)
    {
        lock (_stateGate)
        {
            if (_phase == LaunchPhase.Finishing)
            {
                _logger.LogInformation("Ignoring {Signal}: the launch is already finishing", signal);
                return;
            }

            _stopRequested = true;
            if (_phase == LaunchPhase.Running && _process is not null) TerminateOnce();
        }
    }

    /// <summary>
    /// Terminates the child off the calling thread, because the signal thread must not block through the grace
    /// period. Called with <see cref="_stateGate"/> held, and only the first call does anything.
    /// </summary>
    private void TerminateOnce()
    {
        if (_terminating is not null || _process is not { } process) return;

        _logger.LogInformation("Stopping process {Pid}", process.Pid);
        _terminating = Task.Run(() =>
        {
            try
            {
                process.Terminate();
            }
            catch (Exception e)
            {
                // The wait still ends when the child does, and a survivor is reported from its result.
                _logger.LogError(e, "Terminating process {Pid} failed", process.Pid);
            }
        });
    }

    private void LogPlan(LaunchPlan plan, string game)
    {
        if (plan.Resolved is { } resolved)
            _logger.LogInformation("Launch spec for {Game}: exe {Exe}, extra arguments [{ExtraArgs}]", game, resolved.Exe, string.Join(' ', resolved.ExtraArgs));
        else
            _logger.LogInformation("Launch spec for {Game}: none resolved by a run tool", game);

        if (plan.Substitution is { } substitution)
            _logger.LogInformation("Command substitution for {Game}: app id {AppId}, {Outcome}", game, substitution.AppId ?? "none",
                substitution.Refusal is { } refusal ? $"refused: {refusal}" : $"command [{string.Join(' ', substitution.Argv)}]");
        else
            _logger.LogInformation("Command substitution for {Game}: none", game);

        _logger.LogInformation("Starting {Game}: argv [{Argv}], working directory {WorkingDirectory}", game,
            string.Join(' ', plan.Process.Argv), plan.Process.WorkingDirectory ?? "inherited");
    }

    private static async Task<GameProcessResult> WaitFor(IGameProcess process, CancellationToken cancellationToken)
    {
        try
        {
            return await process.WaitAsync(cancellationToken);
        }
        catch
        {
            // Never leave the game running unattended behind an exception.
            process.Terminate();
            throw;
        }
    }

    /// <summary>
    /// Runs the teardown of every gate that was prepared and then ingests the game folder. A failing gate does not
    /// stop the others or the ingest, so a bad teardown can't cost the user their game's changes. Returns the first
    /// failure.
    /// </summary>
    private async Task<string?> Finish(LaunchContext context, IReadOnlyList<ILaunchGate> gates)
    {
        string? failure = null;
        void Record(Exception e, string what)
        {
            _logger.LogError(e, "{What} failed for {Loadout}", what, context.LoadoutName);
            failure ??= $"{context.Installation.Game.DisplayName} has exited, but {what} failed: {e.Message} " +
                        $"Loadout '{context.LoadoutName}' is still applied.";
        }

        try
        {
            _lock!.WriteState(Environment.ProcessId, context.LoadoutName, LaunchPhase.Finishing);
        }
        catch (Exception e)
        {
            Record(e, "recording the launch state");
        }

        foreach (var gate in gates)
        {
            try
            {
                await gate.Finish(context);
            }
            catch (Exception e)
            {
                Record(e, $"cleaning up after the game ({gate.GetType().Name})");
            }
        }

        try
        {
            await _synchronizer.Synchronize(context.LoadoutId);
        }
        catch (Exception e)
        {
            Record(e, "taking the game's changes back into the loadout");
        }

        return failure;
    }

    private static string DescribeHolder(string game, InstallationLock installationLock)
    {
        InstallationLockState? state = null;
        try
        {
            state = installationLock.ReadState();
        }
        catch (InvalidDataException)
        {
            // A half-written or foreign state file only costs the details.
        }

        return state is null
            ? $"{game} is already being launched by another SilverModManager process."
            : $"{game} is already being launched by SilverModManager process {state.Pid} (loadout '{state.Loadout}', {state.Phase.ToString().ToLowerInvariant()}).";
    }

    /// <summary>
    /// True when a Steam wrapper has written its key into the state file. Read from disk on each call, because
    /// the wrapper is another process.
    /// </summary>
    public bool WrapperActive
    {
        get
        {
            if (_lock is null) return false;
            try
            {
                return _lock.ReadState()?.Values.ContainsKey(WrapperKey) ?? false;
            }
            catch (InvalidDataException)
            {
                // A half-written file has no wrapper we can rely on; the next poll reads it again.
                return false;
            }
        }
    }

    /// <summary>The state file key the Steam wrapper reports its pid under.</summary>
    public const string WrapperKey = "wrapper";

    /// <summary>
    /// Ends the session: deletes the state file, releases the lock and stops reporting the game as running, in
    /// that order. The state file is kept as <c>running</c> when a game process could not be killed, so the
    /// next start can tell the game was never stopped. Safe to call more than once, or without a run.
    /// </summary>
    public void Release()
    {
        lock (_releaseLock)
        {
            if (_released) return;
            _released = true;

            if (_lock is { IsHeld: true } && !_keepRunningState) File.Delete(_lock.StatePath);
            _lock?.Dispose();
            _registration?.Dispose();
        }
    }
}
