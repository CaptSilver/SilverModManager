using System.Reactive.Linq;
using SilverModManager.Wabbajack.Install;

namespace SilverModManager.App.UI.Pages.Wabbajack;

/// <summary>What an install shows while it runs: the phase line and the bar.</summary>
public readonly record struct WabbajackInstallStatus(string Text, double Fraction);

/// <summary>Turns the snapshots of a running install into the status the page and the collection header show.</summary>
public static class WabbajackInstallStatusFeed
{
    /// <summary>One status per snapshot.</summary>
    public static IObservable<WabbajackInstallStatus> Follow(IObservable<ModlistInstallProgress.Snapshot> progress)
        => progress.Select(snapshot => new WabbajackInstallStatus(
            WabbajackModlistText.Progress(snapshot),
            WabbajackModlistText.Fraction(snapshot)));
}
