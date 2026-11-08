using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Loadouts.Synchronizers;

namespace SilverModManager.Platform.Proton.Launch;

/// <summary>
/// What happens after the game has stopped: the gates' teardown, then taking the game folder's changes back into
/// the loadout.
/// </summary>
public sealed class LaunchFinish(ILaunchSynchronizer synchronizer, ILaunchFinishMarkers markers, ILogger<LaunchFinish> logger)
{
    /// <summary>
    /// Runs the teardown of every gate that was prepared and then ingests the game folder. A failing gate does not
    /// stop the others or the ingest, so a bad teardown can't cost the user their game's changes. The pending-finish
    /// marker is cleared last, whether or not the ingest worked. Returns the first failure.
    /// </summary>
    internal async Task<LaunchFailure?> RunAsync(LaunchContext context, IReadOnlyList<ILaunchGate> gates, InstallationLock heldLock, bool callerReports)
    {
        LaunchFailure? failure = null;
        void Record(Exception e, string what)
        {
            // Switching mods off while the game ran leaves the removal question for the sync after it; a "no" there is
            // the player's answer, not a crash, and nothing was taken out of the folder.
            if (e is MassRemovalException refused)
            {
                logger.Log(LogLevel.Warning, refused, "The removal question for {Loadout} after the game exited was refused", context.LoadoutName);
                failure ??= new LaunchFailure($"{context.Installation.Game.DisplayName} has exited. Nothing was removed from the game folder; apply '{context.LoadoutName}' when you want the files it no longer holds taken out.",
                    Refused: true, Cause: e, LoadoutStillApplied: true);
                return;
            }

            LogCause(callerReports, e, "{What} failed for {Loadout}", what, context.LoadoutName);
            if (e is BackupOverflowException { Declined: true })
            {
                failure ??= new LaunchFailure($"{context.Installation.Game.DisplayName} has exited. Loadout '{context.LoadoutName}' is still applied, and the files the game wrote are not in it yet; the app asks about them again when you apply or switch.",
                    Cause: e, LoadoutStillApplied: true);
                return;
            }

            failure ??= new LaunchFailure($"{context.Installation.Game.DisplayName} has exited, but {what} failed: {e.Message} " +
                                          $"Loadout '{context.LoadoutName}' is still applied. Apply or revert it from SilverModManager before playing the game outside it.",
                Cause: e, LoadoutStillApplied: true);
        }

        try
        {
            heldLock.WriteState(Environment.ProcessId, context.LoadoutName, LaunchPhase.Finishing, LaunchSession.WineOverridesOf(context), LaunchSession.RecordedLaunchOf(context));
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
            // Files a game writes into its own folder are the game's: keep them where they are unless keeping
            // would lose files, and never put the launch's wording on a question asked after the game closed.
            using var leg = BackupOverflowAnswer.BeginLeg(BackupOverflowOperation.AfterPlay, BackupOverflowChoice.AdoptInPlace);
            using var origin = SyncOrigin.Begin(SyncTrigger.AfterGameRun);
            await synchronizer.Synchronize(context.LoadoutId);
        }
        catch (Exception e)
        {
            Record(e, "taking the game's changes back into the loadout");
        }

        // A committed sync already cleared the marker, so this usually finds nothing. After a refused or failed
        // sync it is the only thing that does, and a marker left behind would repeat the same failure at every start.
        try
        {
            await markers.Retract(context.Installation);
        }
        catch (Exception e)
        {
            Record(e, "clearing the finish marker");
        }

        return failure;
    }

    // A caught exception goes to the log with its trace: at Error when the run verb is the last stop, at Warning
    // when the awaiting caller shows the failure and the log is only the record.
    private void LogCause(bool callerReports, Exception e, string message, params object?[] args)
        => logger.Log(callerReports ? LogLevel.Warning : LogLevel.Error, e, message, args);
}
