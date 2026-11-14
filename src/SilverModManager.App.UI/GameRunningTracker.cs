using SilverModManager.Platform.Proton.Launch;
using SilverModManager.Sdk.Games;

namespace SilverModManager.App.UI;

/// <summary>
/// Answers whether a game is running, per installation, from the launch registrations of this process.
/// </summary>
/// <remarks>
/// The registrations cover every way the app starts a game, including a launch Steam forwards to us, which is no
/// run-game job. A consumer that needs "any game running" merges the observables of the installations it cares about.
/// </remarks>
public class GameRunningTracker(ActiveLaunchSessions sessions)
{
    /// <summary>
    /// Whether <paramref name="installation"/> has a launch registered: the current value on subscribe, then each change.
    /// </summary>
    public IObservable<bool> WhenRunning(GameInstallation installation) => sessions.WhenRunning(installation);
}
