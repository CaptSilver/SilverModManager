namespace SilverModManager.Platform.Proton.Steam;

public enum SteamCloseOutcome
{
    /// <summary>Steam was closed (or already was) and the write ran.</summary>
    Written,

    /// <summary>Steam was still running when the wait ran out. Nothing was written.</summary>
    TimedOut,

    /// <summary>The caller stopped waiting. Nothing was written.</summary>
    Cancelled,
}

public sealed record SteamCloseAndWriteResult<T>(SteamCloseOutcome Outcome, T? Value, bool SteamRestarted);

/// <summary>
/// Asks a running Steam to exit, waits until it is gone, runs a write against its config, and starts Steam again.
/// </summary>
/// <remarks>
/// Steam rewrites <c>localconfig.vdf</c> as it exits, so an edit made while it runs is lost. Steam is only ever
/// asked to exit; it is never killed, so a Steam that refuses (a dialog, a running download prompt) ends in a
/// timeout rather than in a half-written config.
/// <para>Callbacks and the write run on the caller's context, so a view model can update itself from them.</para>
/// </remarks>
public sealed class SteamCloseAndWrite(
    Func<bool> isSteamRunning,
    Func<bool> areHelpersRunning,
    Action requestExit,
    Action startSteam,
    Func<TimeSpan, CancellationToken, Task> delay,
    TimeSpan pollInterval,
    TimeSpan timeout,
    TimeSpan helperTimeout)
{
    /// <summary>
    /// <paramref name="isSteamRunning"/> should be the same check the write itself refuses on, so the wait ends
    /// exactly when the write becomes possible.
    /// </summary>
    public static SteamCloseAndWrite Create(Func<bool> isSteamRunning, Func<bool> areHelpersRunning, Action requestExit, Action startSteam)
        => new(isSteamRunning, areHelpersRunning, requestExit, startSteam, Task.Delay, TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(10));

    /// <param name="write">Runs once Steam is not running. It is async so a caller can move the file work off its UI thread.</param>
    /// <param name="restartSteam">
    /// Whether to start Steam again after the write, given its result. A write that leaves a second write for the
    /// user to confirm needs Steam to stay closed until then.
    /// </param>
    /// <param name="onWaiting">Called once, after the exit request, if Steam has to be waited for.</param>
    public async Task<SteamCloseAndWriteResult<T>> RunAsync<T>(Func<Task<T>> write, Func<T, bool> restartSteam, CancellationToken cancellationToken, Action? onWaiting = null)
    {
        var wasRunning = isSteamRunning();
        if (wasRunning)
        {
            requestExit();
            onWaiting?.Invoke();
            var outcome = await WaitForExit(cancellationToken);
            if (outcome != SteamCloseOutcome.Written) return new SteamCloseAndWriteResult<T>(outcome, default, SteamRestarted: false);
        }

        // Steam was closed by us; it comes back even if the write throws, so the user is not left without it.
        T value;
        try { value = await write(); }
        catch
        {
            if (wasRunning) await StartSteamAgain();
            throw;
        }

        var restart = wasRunning && restartSteam(value);
        if (restart) await StartSteamAgain();
        return new SteamCloseAndWriteResult<T>(SteamCloseOutcome.Written, value, restart);
    }

    private async Task<SteamCloseOutcome> WaitForExit(CancellationToken cancellationToken)
    {
        var polls = Math.Max(1, (int)Math.Ceiling(timeout / pollInterval));
        try
        {
            for (var i = 0; i < polls; i++)
            {
                await delay(pollInterval, cancellationToken);
                if (!await OffCallerThread(isSteamRunning)) return SteamCloseOutcome.Written;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return SteamCloseOutcome.Cancelled;
        }
        return SteamCloseOutcome.TimedOut;
    }

    /// <remarks>
    /// The main process writing its config is what the write waits on, but steamwebhelper children can outlive it
    /// for a moment. A new Steam started over them shares their browser cache, which they still have locked. A helper
    /// that never goes (left behind by a crash) does not stop the restart for longer than the helper timeout. This
    /// runs after the write, so it is never cancelled: the user is not left without Steam.
    /// </remarks>
    private async Task StartSteamAgain()
    {
        var polls = (int)Math.Ceiling(helperTimeout / pollInterval);
        for (var i = 0; i < polls && await OffCallerThread(areHelpersRunning); i++)
        {
            await delay(pollInterval, CancellationToken.None);
        }
        startSteam();
    }

    // Each check scans /proc. The caller is usually the dialog, so keep the scan off its thread and come back to it.
    private static Task<bool> OffCallerThread(Func<bool> check) => Task.Run(check);
}
