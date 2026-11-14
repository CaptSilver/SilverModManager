using SilverModManager.Abstractions.Games;
using SilverModManager.Platform.Proton.Doctor;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Platform.Proton.Launch;

/// <summary>
/// Looks at every managed game for a mount a dead session left behind and has the pending-finish step clean it up. Runs
/// when the app starts and before the doctor report, so neither shows a folder that is still mounted from an earlier run.
/// </summary>
public sealed class StaleMountSweep(IManagedInstallations installations, IPendingLaunchFinisher finisher)
{
    /// <summary>
    /// One report per managed installation, in the order they were listed. The finisher turns a failure into a report
    /// rather than throwing, so one game cannot stop the sweep of the others.
    /// </summary>
    public Task<IReadOnlyList<LaunchFinishReport>> Run(CancellationToken token) => Run(static _ => true, token);

    /// <summary>
    /// The same, for the installations <paramref name="include"/> keeps. The window leaves out the ones whose marker it
    /// finishes itself, or a finish the game refused would run, and be reported, twice.
    /// </summary>
    public async Task<IReadOnlyList<LaunchFinishReport>> Run(Func<GameInstallation, bool> include, CancellationToken token)
    {
        var reports = new List<LaunchFinishReport>();
        foreach (var managed in installations.List())
        {
            if (!include(managed.Installation)) continue;
            reports.Add(await finisher.FinishIfPending(managed.Installation, token));
        }
        return reports;
    }
}
