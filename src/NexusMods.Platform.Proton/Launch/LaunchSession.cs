using Microsoft.Extensions.Logging;
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
/// One launch of one loadout: takes the installation lock, applies the loadout, runs the game and takes its
/// changes back. The lock is held, and <see cref="ActiveLaunchSessions.IsRunning"/> stays true, until
/// <see cref="Release"/>: the caller closes the database first, so nothing can write to a game folder that
/// another launch has already been let into.
/// </summary>
public sealed class LaunchSession
{
    /// <summary>The exit code for every failure of the launch itself, as opposed to the game's own.</summary>
    public const int FailureExitCode = 125;

    private readonly IInstallationLockFactory _lockFactory;
    private readonly ActiveLaunchSessions _activeSessions;
    private readonly ILaunchSynchronizer _synchronizer;
    private readonly IGameProcessFactory _processFactory;
    private readonly IReadOnlyList<ILaunchGate> _gates;
    private readonly IDesktopNotifier _notifier;
    private readonly ILogger<LaunchSession> _logger;
    private readonly TextWriter _error;
    private readonly object _releaseLock = new();

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
        TextWriter? error = null)
    {
        _lockFactory = lockFactory;
        _activeSessions = activeSessions;
        _synchronizer = synchronizer;
        _processFactory = processFactory;
        _gates = gates.ToList();
        _notifier = notifier;
        _logger = logger;
        _error = error ?? Console.Error;
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
    public async Task<LaunchResult> RunAsync(LaunchContext context, Func<GameProcessSpec> resolveSpec, CancellationToken cancellationToken = default)
    {
        if (_started) throw new InvalidOperationException("A launch session runs once.");
        _started = true;

        var failure = await RunCore(context, resolveSpec, cancellationToken);
        if (failure is null) return _result!;

        _logger.LogError("{Message}", failure);
        _error.WriteLine(failure);
        _notifier.Notify(ApplicationConstants.ProductName, failure);
        return new LaunchResult(FailureExitCode, failure);
    }

    /// <summary>
    /// Returns the failure message, or null with <see cref="_result"/> set when the game ran to the end.
    /// </summary>
    private async Task<string?> RunCore(LaunchContext context, Func<GameProcessSpec> resolveSpec, CancellationToken cancellationToken)
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

        try
        {
            _lock.WriteState(Environment.ProcessId, context.LoadoutName, LaunchPhase.Preparing);
            await _synchronizer.Synchronize(context.LoadoutId);
            foreach (var gate in _gates) await gate.Prepare(context, cancellationToken);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Preparing {Loadout} for {Game} failed", context.LoadoutName, game);
            return $"Could not prepare loadout '{context.LoadoutName}' for {game}: {e.Message}";
        }

        string? startFailure = null;
        GameProcessResult? exit = null;
        try
        {
            var spec = resolveSpec();
            _lock.WriteState(Environment.ProcessId, context.LoadoutName, LaunchPhase.Running);
            using var process = _processFactory.Start(spec, context.Installation);
            exit = await WaitFor(process, cancellationToken);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Running {Game} failed", game);
            startFailure = $"Could not start {game}: {e.Message}";
        }

        if (exit?.KillFailed is { } survivor)
        {
            // The game folder is in use; ingesting it or running the gates' teardown now could corrupt it.
            _keepRunningState = true;
            return $"{game} did not stop: process {survivor.Pid} ({survivor.Name}) survived SIGKILL. " +
                   "The loadout is still applied. Stop that process, then launch again.";
        }

        var finishFailure = await Finish(context);

        if (startFailure is not null) return startFailure;
        if (finishFailure is not null) return finishFailure;

        _result = new LaunchResult(exit!.ExitCode, null);
        return null;
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
    /// Runs every gate's teardown and then ingests the game folder. A failing gate does not stop the others or
    /// the ingest, so a bad teardown can't cost the user their game's changes. Returns the first failure.
    /// </summary>
    private async Task<string?> Finish(LaunchContext context)
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

        foreach (var gate in _gates)
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
