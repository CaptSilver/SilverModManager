using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Games;
using SilverModManager.Platform.Proton.Launch;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Games;
using SilverModManager.UI.Sdk;

namespace SilverModManager.App.UI;

/// <summary>
/// Finishes launches whose process went away after the game started, from the window's side: once when the window
/// opens, and again for an installation whenever a launch in this process ends with its marker still set (the
/// game was still running when the finisher last looked, or Steam was updating it). A finish the kick or the sync
/// hook ran itself does not count as a launch ending. Each result is toasted.
/// </summary>
public sealed class PendingLaunchFinishKick(IPendingLaunchFinisher finisher, ILaunchFinishMarkers markers, IGameRegistry registry,
    ActiveLaunchSessions sessions, IWindowNotificationService toasts, ILogger<PendingLaunchFinishKick> logger) : IDisposable
{
    private static readonly TimeSpan LongToast = TimeSpan.FromSeconds(15);

    private readonly CompositeDisposable _subscriptions = new();
    private int _started;

    /// <summary>Starts the kicks and the toasts. Does nothing after the first call; the window activates more than once.</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;

        _subscriptions.Add(finisher.Reports.ObserveOn(TaskPoolScheduler.Default).Subscribe(ShowToast));
        _ = Task.Run(StartKicking);
    }

    private void StartKicking()
    {
        try
        {
            foreach (var installation in registry.LocateGameInstallations())
            {
                // Subscribe before the first kick: a launch that ends between the two would otherwise go unnoticed.
                // Only a launch ending counts. The finisher registers the installation while it finishes, and
                // answering that would repeat a finish that failed (or left its marker) without end.
                _subscriptions.Add(sessions.WhenLaunchEnded(installation).Subscribe(_ => Task.Run(() => KickIfMarked(installation))));
                KickIfMarked(installation);
            }
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not look for unfinished launches");
        }
    }

    private void KickIfMarked(GameInstallation installation)
    {
        try
        {
            if (markers.Read(installation) is not { } marker) return;
            logger.LogDebug("{Game} has a launch finish pending for loadout {Loadout}; asking the finisher", installation.Game.DisplayName, marker.LoadoutId);
            finisher.FinishIfPending(installation).ContinueWith(
                task => logger.LogWarning(task.Exception, "Finishing the last session of {Game} threw", installation.Game.DisplayName),
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not check {Game} for an unfinished launch", installation.Game.DisplayName);
        }
    }

    // Reports are published from inside the finisher's lock; showing a toast from a pool thread blocks on the UI
    // thread, so the subscription hops to the task pool instead of running in the publisher.
    private void ShowToast(LaunchFinishReport report)
    {
        if (report.Silent || string.IsNullOrEmpty(report.Message)) return;

        var type = report.Outcome switch
        {
            LaunchFinishOutcome.Finished => ToastNotificationVariant.Success,
            LaunchFinishOutcome.ClearedLoadoutGone => ToastNotificationVariant.Neutral,
            _ => ToastNotificationVariant.Failure,
        };
        try
        {
            toasts.ShowToast(MarkdownText.Inline(report.Message), type, type == ToastNotificationVariant.Success ? null : LongToast);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not show the toast for a finished launch");
        }
    }

    public void Dispose() => _subscriptions.Dispose();
}
