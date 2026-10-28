using System.Diagnostics;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using SilverModManager.Sdk.Jobs;
using SilverModManager.UI.Sdk;

namespace SilverModManager.App.UI.Diagnostics;

/// <summary>
/// Posts a no-op to the UI dispatcher from its own thread and logs when the reply is late. The first line is
/// written while the thread is still blocked, so a hang that never ends still leaves a trace with figures
/// taken during it, not after it recovered.
/// </summary>
public sealed class UiStallWatchdog : IDisposable
{
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan DefaultThreshold = TimeSpan.FromMilliseconds(250);

    private readonly Func<Task> _probe;
    private readonly Func<StallFingerprint> _sample;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _threshold;
    private readonly Action<StallFingerprint> _onStall;
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _thread;
    private readonly Action<Exception> _onError;
    private int _started;
    private int _disposed;

    // Set by the loop just before each probe is posted; the sampler reports deltas from them.
    private TimeSpan _gcPauseAtPost;
    private int _gen2AtPost;

    public UiStallWatchdog(ILogger<UiStallWatchdog> logger, IJobMonitor jobs)
    {
        _probe = () => Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Input).GetTask();
        _sample = () => SampleNow(_gcPauseAtPost, _gen2AtPost, jobs);
        _interval = DefaultInterval;
        _threshold = DefaultThreshold;
        _onStall = stall => Log(logger, stall);
        _onError = ex => logger.LogWarning(ex, "UI stall watchdog could not take a sample");
        _thread = new Thread(Loop) { IsBackground = true, Name = "ui-stall-watchdog" };
    }

    internal UiStallWatchdog(Func<Task> probe, Func<StallFingerprint> sample, TimeSpan interval, TimeSpan threshold, Action<StallFingerprint> onStall, Action<Exception> onError)
    {
        _probe = probe;
        _sample = sample;
        _interval = interval;
        _threshold = threshold;
        _onStall = onStall;
        _onError = onError;
        _thread = new Thread(Loop) { IsBackground = true, Name = "ui-stall-watchdog" };
    }

    internal Thread LoopThread => _thread;

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 0) _thread.Start();
    }

    internal static StallFingerprint SampleNow(TimeSpan gcPauseAtPost, int gen2AtPost, IJobMonitor jobs)
    {
        var operation = UiOperation.Current;
        var running = CountRunning(jobs);

        return new StallFingerprint(
            Late: TimeSpan.Zero,
            StillPending: true,
            GcPause: GC.GetTotalPauseDuration() - gcPauseAtPost,
            Gen2Collections: GC.CollectionCount(2) - gen2AtPost,
            PoolThreads: ThreadPool.ThreadCount,
            PendingWorkItems: ThreadPool.PendingWorkItemCount,
            RunningJobs: running,
            UiOperation: operation?.Name,
            UiOperationAge: operation is { } op ? Stopwatch.GetElapsedTime(op.StartedAt) : null);
    }

    /// <summary>
    /// Job threads add to the bound collection while this runs, and <c>ToArray</c> can lose that race. Retry a few
    /// times, then report -1 rather than let a diagnostic throw.
    /// </summary>
    private static int CountRunning(IJobMonitor jobs)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                return jobs.Jobs.ToArray().Count(job => job.Status == JobStatus.Running);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                // The collection changed under the copy; try again.
            }
        }

        return -1;
    }

    private void Loop()
    {
        var token = _stop.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    ProbeOnce(token);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // A diagnostic must not take the process down; report it and keep watching.
                    try { _onError(ex); } catch (Exception) { /* nothing left to report to */ }
                }

                token.WaitHandle.WaitOne(_interval);
            }
        }
        catch (OperationCanceledException)
        {
            // Dispose asked the loop to stop.
        }
    }

    private void ProbeOnce(CancellationToken token)
    {
        _gcPauseAtPost = GC.GetTotalPauseDuration();
        _gen2AtPost = GC.CollectionCount(2);
        var started = Stopwatch.GetTimestamp();
        var probe = _probe();

        if (!Wait(probe, _threshold, token))
        {
            // The wait timed out, so at least the threshold has passed whatever the timer granularity says.
            var late = Stopwatch.GetElapsedTime(started);
            _onStall(_sample() with { Late = late < _threshold ? _threshold : late, StillPending = true });
            Wait(probe, Timeout.InfiniteTimeSpan, token);
            _onStall(_sample() with { Late = Stopwatch.GetElapsedTime(started), StillPending = false });
        }
    }

    private static bool Wait(Task probe, TimeSpan timeout, CancellationToken token)
    {
        try
        {
            return probe.Wait(timeout, token);
        }
        catch (AggregateException)
        {
            // A probe that faulted still means the dispatcher is not blocked on it.
            return true;
        }
    }

    private static void Log(ILogger logger, StallFingerprint s)
    {
        if (s.StillPending)
        {
            logger.LogWarning(
                "UI thread has not answered for {Late} ms (GC pause {GcPause} ms, {Gen2} gen2 collections, pool {PoolThreads} threads / {Pending} pending, {Jobs} jobs running, UI operation {Operation} for {Age} ms)",
                (long)s.Late.TotalMilliseconds, (long)s.GcPause.TotalMilliseconds, s.Gen2Collections, s.PoolThreads, s.PendingWorkItems, s.RunningJobs,
                s.UiOperation ?? "none", s.UiOperationAge is { } age ? (long)age.TotalMilliseconds : 0);
        }
        else
        {
            logger.LogWarning("UI thread answered after {Total} ms", (long)s.Late.TotalMilliseconds);
        }
    }

    public void Dispose()
    {
        // Both App's exit handler and the container dispose this; the second call must be a no-op.
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _stop.Cancel();
        var stopped = true;
        if (Volatile.Read(ref _started) == 1 && Thread.CurrentThread != _thread) stopped = _thread.Join(TimeSpan.FromSeconds(2));
        // A loop stuck in a callback still holds the token's wait handle; leave the source for the finalizer then.
        if (stopped) _stop.Dispose();
    }
}
