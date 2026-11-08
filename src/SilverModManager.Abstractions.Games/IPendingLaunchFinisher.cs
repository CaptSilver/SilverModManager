using SilverModManager.Sdk.Games;

namespace SilverModManager.Abstractions.Games;

/// <summary>How a look for an unfinished launch ended.</summary>
public enum LaunchFinishOutcome
{
    /// <summary>No marker was set, or a finish is already running in this call chain.</summary>
    NothingPending,

    /// <summary>The game's changes were taken back into the loadout.</summary>
    Finished,

    /// <summary>A sync had been committed since the marker was written, so the marker was only cleared.</summary>
    ClearedAlreadySynced,

    /// <summary>The marked loadout was deleted, so the marker was cleared.</summary>
    ClearedLoadoutGone,

    /// <summary>Steam is updating the game; the marker stays for the next try.</summary>
    WaitingForSteam,

    /// <summary>A game process is still running; the marker stays for the next try.</summary>
    GameRunning,

    /// <summary>Another holder, or a launch in this process, has the game; the marker stays.</summary>
    SkippedHeld,

    /// <summary>The finish ran and something in it failed.</summary>
    Failed,
}

/// <summary>What <see cref="IPendingLaunchFinisher.FinishIfPending"/> found and did.</summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="Installation">The game the look was for.</param>
/// <param name="Message">The one line to show the person, or null when there is nothing to say.</param>
/// <param name="Cause">The exception behind a failure, if there was one.</param>
/// <param name="LoadoutStillApplied">Whether the loadout is still applied to the game folder after a failed finish.</param>
public sealed record LaunchFinishReport(LaunchFinishOutcome Outcome, GameInstallation Installation, string? Message = null, Exception? Cause = null, bool LoadoutStillApplied = false)
{
    /// <summary>True for outcomes the person has no need to hear about.</summary>
    public bool Silent => Outcome is LaunchFinishOutcome.NothingPending or LaunchFinishOutcome.SkippedHeld or LaunchFinishOutcome.ClearedAlreadySynced;
}

/// <summary>
/// Finishes a launch whose process went away after the game started, by taking the game folder's changes back
/// into the loadout that was applied for it.
/// </summary>
public interface IPendingLaunchFinisher
{
    /// <summary>
    /// Finishes the installation's unfinished launch if there is one and the game is no longer running. Never
    /// throws for a finish that fails; the report says so.
    /// </summary>
    Task<LaunchFinishReport> FinishIfPending(GameInstallation installation, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every report a call that took its own installation lock produced, except the silent ones. The window shows
    /// them and the command line prints them.
    /// </summary>
    IObservable<LaunchFinishReport> Reports { get; }
}
