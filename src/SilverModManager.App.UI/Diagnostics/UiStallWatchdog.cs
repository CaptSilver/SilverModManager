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
    private int _started;

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
        _thread = new Thread(Loop) { IsBackground = true, Name = "ui-stall-watchdog" };
    }

    internal UiStallWatchdog(Func<Task> probe, Func<StallFingerprint> sample, TimeSpan interval, TimeSpan threshold, Action<StallFingerprint> onStall)
    {
        _probe = probe;
        _sample = sample;
        _interval = interval;
        _threshold = threshold;
        _onStall = onStall;
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
        var running = 0;
        foreach (var job in jobs.Jobs.ToArray())
        {
            if (job.Status == JobStatus.Running) running++;
        }

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

    private void Loop()
    {
        var token = _stop.Token;
        try
        {
            while (!token.IsCancellationRequested)
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

                token.WaitHandle.WaitOne(_interval);
            }
        }
        catch (OperationCanceledException)
        {
            // Dispose asked the loop to stop.
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
        _stop.Cancel();
        if (Volatile.Read(ref _started) == 1 && Thread.CurrentThread != _thread) _thread.Join(TimeSpan.FromSeconds(2));
        _stop.Dispose();
    }
}
