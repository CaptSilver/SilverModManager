using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Games;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.Platform.Proton.Launch;

/// <summary>
/// How a launch ended.
/// </summary>
/// <param name="ExitCode">The game's exit code, or 125 when the launch itself failed.</param>
/// <param name="Message">Why the launch failed; null when it ran and the game's own exit code is the answer.</param>
/// <param name="Refused">True when the launch was turned down before anything was started, by a gate or another launch holding the game.</param>
/// <param name="Cause">The exception a failing step threw, when there was one; its message is folded into <paramref name="Message"/>.</param>
public sealed record LaunchResult(int ExitCode, string? Message, bool Refused = false, Exception? Cause = null);

/// <summary>Why a launch failed, before it is reported.</summary>
internal sealed record LaunchFailure(string Message, bool Refused = false, Exception? Cause = null);

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
    private readonly INexusCredentialSummary? _credentials;

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
    // True when the caller awaits the launch and shows its failure, as the app does; the run verb has no one else to tell the user.
    private bool _callerReports;
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
        StopHandler? stop = null,
        INexusCredentialSummary? credentials = null)
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
        _credentials = credentials;
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

    /// <summary>
    /// Writes what <see cref="RunAsync(LaunchContext, Func{LaunchPlan}, CancellationToken)"/> would do to
    /// <paramref name="output"/>, and does none of it: no lock is taken, nothing is applied and nothing is started,
    /// so it also works while another launch holds the installation.
    /// </summary>
    /// <remarks>
    /// The plan is resolved against the loadout as it stands, while a real launch applies it first, so a run tool
    /// that looks at files the apply puts in place can resolve differently there. The output says so.
    /// A plan that cannot be resolved fails the way a launch does, with <see cref="FailureExitCode"/>.
    /// </remarks>
    public LaunchResult DryRun(LaunchContext context, Func<LaunchPlan> resolvePlan, TextWriter output)
    {
        var game = context.Installation.Game.DisplayName;

        LaunchPlan plan;
        try
        {
            plan = resolvePlan();
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Planning the launch of {Loadout} for {Game} failed", context.LoadoutName, game);
            return ReportFailure(new LaunchFailure($"Cannot plan the launch of {game}: {e.Message}", Cause: e));
        }

        output.WriteLine("Dry run: nothing was locked, applied or started.");
        output.WriteLine($"Game: {game}");
        output.WriteLine($"Loadout: {context.LoadoutName}");
        output.WriteLine(DescribeCredential());
        output.WriteLine("Before the game starts: one synchronize applies the loadout. After it exits: one synchronize takes its changes back.");

        if (plan.Resolved is { } resolved)
            output.WriteLine(resolved.ExtraArgs.Length == 0 ? $"Run tool: {resolved.Exe}" : $"Run tool: {resolved.Exe} with arguments {string.Join(' ', resolved.ExtraArgs)}");
        else
            output.WriteLine("Run tool: none, the command is started as given");

        output.WriteLine(plan.Substitution is { } substitution ? $"App id: {substitution.AppId ?? "none"}" : "App id: none");
        output.WriteLine($"Command: {string.Join(' ', plan.Process.Argv)}");
        output.WriteLine(plan.Process.WineDllOverrides is { Count: > 0 } overrides ? $"Wine DLL overrides: {string.Join(';', overrides)}" : "Wine DLL overrides: none");
        if (plan.Process.WorkingDirectory is { } workingDirectory) output.WriteLine($"Working directory: {workingDirectory}");
        output.WriteLine("The command was resolved against the loadout as it is now; a real launch applies the loadout first, which can change it.");
        return new LaunchResult(0, null);
    }

    private static string WineOverridesOf(LaunchContext context) => string.Join(';', context.WineDllOverrides);

    private static RecordedLaunch? RecordedLaunchOf(LaunchContext context)
    {
        if (context.RunSpec is not { } spec) return null;

        var root = context.Installation.Locations[LocationId.Game].Path.GetFullPath();
        var exe = Path.GetRelativePath(root, spec.Exe.GetFullPath());
        // A wrapper joins this path onto the game folder, so one that leads out of it would start something else.
        if (exe.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(exe))
            throw new InvalidOperationException($"The run tool's executable {spec.Exe} is not inside the game folder {root}");
        return new RecordedLaunch(root, exe, spec.ExtraArgs);
    }

    private async Task<LaunchResult> Run(LaunchContext context, Func<LaunchPlan>? resolvePlan, Func<CancellationToken, Task>? whileRunning, CancellationToken cancellationToken)
    {
        if (_started) throw new InvalidOperationException("A launch session runs once.");
        _started = true;
        _callerReports = whileRunning is not null;

        LaunchFailure? failure;
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

        return ReportFailure(failure);
    }

    private LaunchResult ReportFailure(LaunchFailure failure)
    {
        // A crash's exception was logged where it was caught; this line only says what the user is told. When the
        // caller shows the failure, neither the log line nor anything else here may look like a second crash report.
        // A surviving process carries a cause only for the awaiting caller; nothing logged it, so this line is its record.
        var logged = failure.Cause is not (null or GameDidNotStopException);
        var level = failure.Refused || logged || _callerReports ? LogLevel.Warning : LogLevel.Error;
        _logger.Log(level, "{Message}", failure.Message);
        if (!_callerReports)
        {
            _error.WriteLine(failure.Message);
            _notifier.Notify(ApplicationConstants.ProductName, failure.Message);
        }
        return new LaunchResult(FailureExitCode, failure.Message, failure.Refused, failure.Cause);
    }

    // A caught exception goes to the log with its trace: at Error when the run verb is the last stop, at Warning
    // when the awaiting caller shows the failure and the log is only the record.
    private void LogCause(Exception e, string message, params object?[] args)
        => _logger.Log(_callerReports ? LogLevel.Warning : LogLevel.Error, e, message, args);

    /// <summary>
    /// Returns the failure, or null with <see cref="_result"/> set when the game ran to the end.
    /// </summary>
    private async Task<LaunchFailure?> RunCore(LaunchContext context, Func<LaunchPlan>? resolvePlan, Func<CancellationToken, Task>? whileRunning, CancellationToken cancellationToken)
    {
        var game = context.Installation.Game.DisplayName;

        try
        {
            _lock = _lockFactory.Create(context.Installation.LocatorResult.Store.Value.ToLowerInvariant(), context.Installation.LocatorResult.StoreIdentifier);
            if (!_lock.TryAcquire()) return new LaunchFailure(DescribeHolder(game, _lock), Refused: true);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException)
        {
            LogCause(e, "Taking the launch lock for {Game} failed", game);
            return new LaunchFailure($"Cannot start {game}: the launch lock is unavailable. {e.Message}", Cause: e);
        }

        _registration = _activeSessions.Register(context.Installation);

        foreach (var gate in _gates)
        {
            try
            {
                if (gate.Check(context) is { } veto) return new LaunchFailure(veto.Message, Refused: true);
            }
            catch (Exception e)
            {
                LogCause(e, "Checking {Gate} for {Game} failed", gate.GetType().Name, game);
                return new LaunchFailure($"Cannot start {game}: checking whether it can be launched failed ({gate.GetType().Name}). {e.Message}", Cause: e);
            }
        }

        var prepared = new List<ILaunchGate>();
        try
        {
            _lock.WriteState(Environment.ProcessId, context.LoadoutName, LaunchPhase.Preparing, WineOverridesOf(context), RecordedLaunchOf(context));
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
            LogCause(e, "Preparing {Loadout} for {Game} failed", context.LoadoutName, game);
            return new LaunchFailure($"Could not prepare loadout '{context.LoadoutName}' for {game}: {e.Message}", Cause: e);
        }

        LaunchFailure? startFailure = null;
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
                        _lock.WriteState(Environment.ProcessId, context.LoadoutName, LaunchPhase.Running, WineOverridesOf(context), RecordedLaunchOf(context));
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
                    _lock.WriteState(Environment.ProcessId, context.LoadoutName, LaunchPhase.Running, WineOverridesOf(context), RecordedLaunchOf(context));
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
                LogCause(e, "Running {Game} failed", game);
                startFailure = new LaunchFailure($"Could not start {game}: {e.Message}", Cause: e);
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
            var message = $"{game} did not stop: process {survivor.Pid} ({survivor.Name}) survived SIGKILL. " +
                          "The loadout is still applied. Stop that process, then launch again.";
            // The cause makes an awaiting caller show this as "the game did not stop" and not as a launch that never started.
            return new LaunchFailure(message, Cause: new GameDidNotStopException(message));
        }

        var stopped = BeginFinishing();
        var finishFailure = await Finish(context, prepared);

        if (startFailure is not null) return startFailure;
        if (finishFailure is not null) return finishFailure;

        _result = new LaunchResult(stopped ? StoppedExitCode : exit!.ExitCode, null);
        return null;
    }

    /// <summary>
    /// Stops the launch the way a termination signal would, for an owner that has no <see cref="StopHandler"/>
    /// (the window, asked to stop over IPC). Same phase rules as the signal: a launch still applying finishes
    /// the apply and never starts the game, a running game is terminated, and a launch already finishing is left
    /// alone. Safe to call before the launch starts and more than once.
    /// </summary>
    public void RequestStop() => OnStopping(PosixSignal.SIGTERM, LaunchPhase.Preparing);

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

    /// <summary>
    /// Which Nexus credential this process holds and where it comes from, read from what is already loaded.
    /// A launch itself calls Nexus never; the line is how a headless run shows its credential path works.
    /// </summary>
    private string DescribeCredential() => _credentials is { Kind: not NexusCredentialKind.None } credentials
        ? $"Nexus credential: {(credentials.Kind == NexusCredentialKind.ApiKey ? "API key" : "OAuth token")} from {credentials.Source}"
        : "Nexus credential: none";

    private void LogPlan(LaunchPlan plan, string game)
    {
        _logger.LogInformation("{Credential}", DescribeCredential());

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
    private async Task<LaunchFailure?> Finish(LaunchContext context, IReadOnlyList<ILaunchGate> gates)
    {
        LaunchFailure? failure = null;
        void Record(Exception e, string what)
        {
            LogCause(e, "{What} failed for {Loadout}", what, context.LoadoutName);
            failure ??= new LaunchFailure($"{context.Installation.Game.DisplayName} has exited, but {what} failed: {e.Message} " +
                                          $"Loadout '{context.LoadoutName}' is still applied.", Cause: e);
        }

        try
        {
            _lock!.WriteState(Environment.ProcessId, context.LoadoutName, LaunchPhase.Finishing, WineOverridesOf(context), RecordedLaunchOf(context));
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
