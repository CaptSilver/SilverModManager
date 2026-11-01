using System.Reactive.Linq;
using SilverModManager.Wabbajack.Install;

namespace SilverModManager.App.UI.Pages.Wabbajack;

/// <summary>What an install shows while it runs: the phase line, the bar and the time left (empty until there is enough to estimate from).</summary>
public readonly record struct WabbajackInstallStatus(string Text, double Fraction, string EtaText);

/// <summary>Turns the snapshots of a running install into the status the page and the collection header show.</summary>
public static class WabbajackInstallStatusFeed
{
    /// <summary>
    /// One status per snapshot. Each subscription estimates from its own start, and stamps snapshots with
    /// <paramref name="time"/> as they arrive.
    /// </summary>
    public static IObservable<WabbajackInstallStatus> Follow(IObservable<ModlistInstallProgress.Snapshot> progress, TimeProvider time)
        => Observable.Defer(() =>
        {
            var estimator = new InstallEstimator();
            return progress.Select(snapshot => new WabbajackInstallStatus(
                WabbajackModlistText.Progress(snapshot),
                WabbajackModlistText.Fraction(snapshot),
                WabbajackModlistText.Eta(estimator.Add(time.GetUtcNow(), snapshot))));
        });
}
