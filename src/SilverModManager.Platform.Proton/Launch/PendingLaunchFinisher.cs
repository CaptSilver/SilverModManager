using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Games;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Platform.Proton.Launch;

/// <summary>
/// Takes the game folder's changes back into the loadout for a launch whose process died after the game started:
/// the pending-finish marker says the ingest is owed, and this runs it once the game is gone.
/// </summary>
/// <remarks>
/// One call runs at a time, and a call made while its own finish is running (the finish's sync passes through the
/// same hook) returns at once. The re-entrancy flag is checked before the semaphore: the other way round, the
/// finish's own sync would wait on the semaphore it already holds.
/// </remarks>
public sealed class PendingLaunchFinisher : IPendingLaunchFinisher
{
    private readonly IConnection connection;
    private readonly ILaunchFinishMarkers markers;
    private readonly IInstallationLockFactory locks;
    private readonly IProcessTracker tracker;
    private readonly ActiveLaunchSessions sessions;
    private readonly IEnumerable<ILaunchGate> gates;
    private readonly LaunchFinish finish;
    private readonly ILogger<PendingLaunchFinisher> logger;
    private readonly OwnProcessFamily? family;
    private readonly Func<LoadoutId, IReadOnlyList<WineDllOverride>>? wineOverridesOf;

    // Public so LaunchSession, a public type, can take it and the container can build it; the optional pieces are
    // for tests and stay internal.
    public PendingLaunchFinisher(IConnection connection, ILaunchFinishMarkers markers, IInstallationLockFactory locks, IProcessTracker tracker,
        ActiveLaunchSessions sessions, IEnumerable<ILaunchGate> gates, LaunchFinish finish, ILogger<PendingLaunchFinisher> logger)
        : this(connection, markers, locks, tracker, sessions, gates, finish, logger, null, null)
    {
    }

    internal PendingLaunchFinisher(IConnection connection, ILaunchFinishMarkers markers, IInstallationLockFactory locks, IProcessTracker tracker,
        ActiveLaunchSessions sessions, IEnumerable<ILaunchGate> gates, LaunchFinish finish, ILogger<PendingLaunchFinisher> logger,
        OwnProcessFamily? family, Func<LoadoutId, IReadOnlyList<WineDllOverride>>? wineOverridesOf)
    {
        this.connection = connection;
        this.markers = markers;
        this.locks = locks;
        this.tracker = tracker;
        this.sessions = sessions;
        this.gates = gates;
        this.finish = finish;
        this.logger = logger;
        this.family = family;
        this.wineOverridesOf = wineOverridesOf;
    }

    private readonly SemaphoreSlim _one = new(1, 1);
    private readonly AsyncLocal<bool> _finishing = new();
    private readonly Subject<LaunchFinishReport> _reports = new();

    /// <inheritdoc />
    public IObservable<LaunchFinishReport> Reports => _reports;

    /// <inheritdoc />
    public async Task<LaunchFinishReport> FinishIfPending(GameInstallation installation, CancellationToken cancellationToken = default)
    {
        var report = await Guarded(installation, heldLock: null, callerReports: false, cancellationToken);
        Publish(report);
        return report;
    }

    /// <summary>
    /// The entry for a caller that already holds the installation lock. Leaves the lock to its owner. Once a finish
    /// will run it reports the Finishing phase on the caller's registration, so the caller restores its own phase
    /// afterwards.
    /// </summary>
    /// <remarks>
    /// With <paramref name="callerReports"/> false the caller shows the report itself (the run verb sends a desktop
    /// notification) and nothing is published. With it true the caller is a window launch, which has no notifier of its
    /// own, so a finish that let the launch go on is published for the window's toast. A report the launch refuses
    /// with (still running, Steam updating, failed) comes back to the caller as the launch's own failure and is not
    /// published, or it would be shown twice.
    /// </remarks>
    public async Task<LaunchFinishReport> FinishIfPending(GameInstallation installation, InstallationLock heldLock, bool callerReports)
    {
        var report = await Guarded(installation, heldLock, callerReports, CancellationToken.None);
        if (callerReports && report.Outcome is LaunchFinishOutcome.Finished or LaunchFinishOutcome.ClearedLoadoutGone) Publish(report);
        return report;
    }

    private void Publish(LaunchFinishReport report)
    {
        if (report.Silent) return;
        // Published outside the semaphore, so two finishes can reach here together; Rx wants one notification at a time.
        lock (_reports) _reports.OnNext(report);
    }

    private async Task<LaunchFinishReport> Guarded(GameInstallation installation, InstallationLock? heldLock, bool callerReports, CancellationToken cancellationToken)
    {
        if (_finishing.Value) return new LaunchFinishReport(LaunchFinishOutcome.NothingPending, installation);

        await _one.WaitAsync(cancellationToken);
        try
        {
            // Checked under the semaphore so a call that queued behind a finish sees the marker it left, not a registration.
            // A launch in this process holds the game and runs the finish itself; without this the exit of every live
            // game would pay for a process-table scan and the lock's retry pause to learn what we already know.
            if (heldLock is null && sessions.IsRunning(installation))
            {
                logger.LogTrace("{Game} is held by a launch in this process; no pending finish to look for", installation.Game.DisplayName);
                return new LaunchFinishReport(LaunchFinishOutcome.SkippedHeld, installation);
            }

            _finishing.Value = true;
            return await Core(installation, heldLock, callerReports);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "Looking for an unfinished launch of {Game} failed", installation.Game.DisplayName);
            return new LaunchFinishReport(LaunchFinishOutcome.Failed, installation,
                $"{installation.Game.DisplayName} closed before its changes were taken back into its loadout, and finishing that failed: {e.Message}", e);
        }
        finally
        {
            _finishing.Value = false;
            _one.Release();
        }
    }

    private async Task<LaunchFinishReport> Core(GameInstallation installation, InstallationLock? heldLock, bool callerReports)
    {
        var game = installation.Game.DisplayName;
        LaunchFinishReport Report(LaunchFinishOutcome outcome, string? message = null)
            => new(outcome, installation, message);

        if (markers.Read(installation) is not { } marker)
        {
            logger.LogTrace("No pending finish marker on {Game}", game);
            return Report(LaunchFinishOutcome.NothingPending);
        }

        switch (marker.State)
        {
            case PendingLaunchFinishState.AlreadySynced:
                logger.LogInformation("Clearing the pending finish marker on {Game}: a sync was committed after it was written", game);
                await markers.Retract(installation);
                return Report(LaunchFinishOutcome.ClearedAlreadySynced);
            case PendingLaunchFinishState.LoadoutGone:
                logger.LogInformation("Clearing the pending finish marker on {Game}: its loadout was deleted", game);
                await markers.Retract(installation);
                return Report(LaunchFinishOutcome.ClearedLoadoutGone,
                    $"{game} closed before its changes were taken back into {NameOf(marker)}, and that loadout has since been deleted. The files are still in the game folder.");
        }

        var name = marker.LoadoutName ?? "its loadout";
        try
        {
            return await RunPending(installation, marker, name, heldLock, callerReports);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The marker is pending and the game is gone, so the loadout is still applied whatever threw.
            logger.LogWarning(e, "Finishing the last session of {Game} failed unexpectedly", game);
            return new LaunchFinishReport(LaunchFinishOutcome.Failed, installation,
                $"{game} closed before its changes were taken back into '{name}', and finishing that failed: {e.Message}", e, LoadoutStillApplied: true);
        }
    }

    private async Task<LaunchFinishReport> RunPending(GameInstallation installation, PendingLaunchFinish marker, string name, InstallationLock? heldLock, bool callerReports)
    {
        var game = installation.Game.DisplayName;
        LaunchFinishReport Report(LaunchFinishOutcome outcome, string? message = null, Exception? cause = null, bool stillApplied = false)
            => new(outcome, installation, message, cause, stillApplied);

        if (installation.LocatorResult.StoreUpdatePending || SteamInstallStepRunning(installation))
        {
            logger.LogWarning("Steam is updating {Game}; its pending finish waits", game);
            return Report(LaunchFinishOutcome.WaitingForSteam,
                $"Steam is updating {game}, so the changes from your last session are not in '{name}' yet. The app finishes this after the update, the next time it applies or launches.");
        }

        var foreign = (family ?? OwnProcessFamily.Current).Foreign(tracker.FindGameProcesses(installation));
        if (foreign.Count > 0)
        {
            logger.LogWarning("{Game} is still running ({Processes}); its pending finish waits", game, string.Join(", ", foreign.Select(p => $"{p.Name}:{p.Pid}")));
            return Report(LaunchFinishOutcome.GameRunning,
                $"{game} is still running, so the changes from your last session are not in '{name}' yet. Quit the game; the app finishes this the next time it applies or launches.");
        }

        var ownLock = heldLock is null;
        var lockToUse = heldLock;
        IDisposable? registration = null;
        try
        {
            if (lockToUse is null)
            {
                try
                {
                    lockToUse = locks.Create(installation.LocatorResult.Store.Value.ToLowerInvariant(), installation.LocatorResult.StoreIdentifier);
                    if (!lockToUse.TryAcquire())
                    {
                        logger.LogDebug("Another holder has the launch lock for {Game}; its pending finish waits", game);
                        return Report(LaunchFinishOutcome.SkippedHeld);
                    }
                }
                catch (Exception e) when (e is IOException or InvalidOperationException)
                {
                    logger.LogWarning(e, "Taking the launch lock for {Game} to finish a launch failed", game);
                    return Report(LaunchFinishOutcome.Failed, $"{game} closed before its changes were taken back into '{name}', and the launch lock is unavailable: {e.Message}", e);
                }

                // Not a launch: the window re-kicks when a launch ends, and must not answer this finish's own end.
                registration = sessions.RegisterFinish(installation);
            }

            sessions.ReportPhase(installation, LaunchPhase.Finishing);

            var named = marker.GateNames.ToHashSet();
            var chosen = gates.Where(g => named.Contains(g.GetType().Name)).ToList();
            foreach (var unknown in named.Except(chosen.Select(g => g.GetType().Name)))
                logger.LogWarning("The pending finish of {Game} names gate {Gate}, which is not registered; skipping it", game, unknown);
            logger.LogTrace("Finishing {Game} for loadout {Loadout} with gates [{Gates}]", game, name, string.Join(", ", chosen.Select(g => g.GetType().Name)));

            var context = new LaunchContext(installation, marker.LoadoutId, name, WineOverrides(installation, marker.LoadoutId), SessionId: marker.SessionId, EndSeen: false);
            var failure = await finish.RunAsync(context, chosen, lockToUse, callerReports);
            if (failure is not null)
            {
                logger.LogWarning(failure.Cause, "Finishing the last session of {Game} failed: {Message}", game, failure.Message);
                return Report(LaunchFinishOutcome.Failed, failure.Message, failure.Cause, failure.LoadoutStillApplied);
            }

            logger.LogInformation("Took the changes from the last session of {Game} back into '{Loadout}'", game, name);
            return Report(LaunchFinishOutcome.Finished, $"{game} closed before its changes were taken back into '{name}'. That is done now.");
        }
        finally
        {
            // The order a launch session releases in: state file, then the lock, then the registration.
            if (ownLock && lockToUse is { IsHeld: true })
            {
                File.Delete(lockToUse.StatePath);
                lockToUse.Dispose();
            }
            registration?.Dispose();
        }
    }

    private static string NameOf(PendingLaunchFinish marker) => marker.LoadoutName is { } name ? $"'{name}'" : "its loadout";

    private bool SteamInstallStepRunning(GameInstallation installation)
        => installation.LocatorResult.Store == GameStore.Steam
           && uint.TryParse(installation.LocatorResult.StoreIdentifier, out var appId)
           && tracker.IsInstallStepRunning(appId);

    private IReadOnlyList<WineDllOverride> WineOverrides(GameInstallation installation, LoadoutId loadoutId)
        => wineOverridesOf is not null
            ? wineOverridesOf(loadoutId)
            : WineDllOverrideRequirements.For(installation.Game, Loadout.Load(connection.Db, loadoutId));
}
