using Avalonia.Threading;

namespace SilverModManager.UI.Tests.Framework;

/// <summary>
/// Tells a UI thread that is spinning apart from one that is idle. A job that never returns (a render tick that
/// keeps finding another frame to draw) leaves every test that awaits the dispatcher waiting for good, and the host
/// sits there with no output until someone kills it. The monitor posts a heartbeat job to the dispatcher; when the
/// thread has not run one for the stall limit, it is stuck, and the monitor says what it was last asked to do.
/// </summary>
public sealed class UiThreadMonitor : IDisposable
{
    /// <summary>Long enough that a slow layout under load is not mistaken for a hang.</summary>
    public static readonly TimeSpan DefaultStallLimit = TimeSpan.FromSeconds(60);

    /// <summary>A UI thread under a loaded machine can be a minute late; an action may take at least this long before it is cut off.</summary>
    public static TimeSpan ActionCap(TimeSpan retryWindow) => retryWindow > TimeSpan.FromMinutes(2) ? retryWindow : TimeSpan.FromMinutes(2);

    private static readonly Lock SharedGate = new();
    private static UiThreadMonitor? _shared;

    private readonly TimeSpan _stallLimit;
    private readonly TimeSpan _interval;
    private readonly Thread _thread;
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _gate = new();
    private readonly HashSet<string> _running = [];
    private long _lastBeatTicks = Environment.TickCount64;
    private int _beatPending;
    private string _lastOperation = "nothing yet";
    private string? _stuckMessage;

    public UiThreadMonitor(TimeSpan stallLimit, TimeSpan interval)
    {
        _stallLimit = stallLimit;
        _interval = interval;
        _thread = new Thread(Watch) { IsBackground = true, Name = nameof(UiThreadMonitor) };
        _thread.Start();
    }

    /// <summary>The monitor every UI test shares: there is one dispatcher for the whole run.</summary>
    public static UiThreadMonitor Shared
    {
        get
        {
            lock (SharedGate) return _shared ??= new UiThreadMonitor(DefaultStallLimit, TimeSpan.FromSeconds(1));
        }
    }

    /// <summary>The explanation once the UI thread was found stuck, otherwise null.</summary>
    public string? StuckMessage
    {
        get { lock (_gate) return _stuckMessage; }
    }

    /// <summary>Notes a test that uses the UI thread, so a stall names who was running.</summary>
    public void TestStarted(string name)
    {
        ThrowIfStuck();
        lock (_gate) _running.Add(name);
    }

    public void TestFinished(string name)
    {
        lock (_gate) _running.Remove(name);
    }

    /// <summary>Notes what the UI thread is about to be asked to do, so a stall can say where it stopped.</summary>
    public void Operation(string description)
    {
        lock (_gate) _lastOperation = description;
    }

    /// <summary>
    /// Waits for <paramref name="work"/> for at most <paramref name="limit"/>. A UI thread that stopped running jobs
    /// never completes it, so the wait ends with an explanation naming the last thing the thread was asked to do
    /// instead of a bare timeout.
    /// </summary>
    public async Task WithinAsync(Task work, TimeSpan limit)
    {
        try
        {
            await work.WaitAsync(limit);
        }
        catch (TimeoutException)
        {
            string last;
            lock (_gate) last = _lastOperation;
            throw new TimeoutException($"The action did not finish within {limit.TotalSeconds:F0} s. If the UI thread is spinning, the last thing it was asked to do was: {last}.");
        }
    }

    /// <summary>Throws the explanation if the UI thread has been found stuck; every later UI test fails at once.</summary>
    public void ThrowIfStuck()
    {
        if (StuckMessage is { } message) throw new UiThreadStuckException(message);
    }

    private void Watch()
    {
        while (!_stop.IsCancellationRequested)
        {
            // One heartbeat in flight: a stuck thread must not collect a queue of them.
            if (Interlocked.Exchange(ref _beatPending, 1) == 0)
                Dispatcher.UIThread.Post(Beat, DispatcherPriority.Background);

            var stalled = TimeSpan.FromMilliseconds(Environment.TickCount64 - Interlocked.Read(ref _lastBeatTicks));
            if (stalled >= _stallLimit)
            {
                string? report = null;
                lock (_gate)
                {
                    if (_stuckMessage is null)
                    {
                        var running = _running.Count == 0 ? "no test" : string.Join(", ", _running);
                        _stuckMessage = report =
                            $"The UI thread has not run a job for {stalled.TotalSeconds:F0} s while {running} was running; " +
                            $"the last thing it was asked to do was: {_lastOperation}. It is spinning, most likely in a render tick " +
                            "that keeps finding another frame to draw (an endless animation such as an indeterminate ProgressBar is the " +
                            "usual cause). Every UI test that starts while it is stuck fails at once.";
                    }
                }

                // The failing test shows only that it timed out; this line says why. Once per stall.
                if (report is not null) Console.Error.WriteLine(report);
            }
            else
            {
                // A thread that was only slow and runs jobs again is not stuck: later tests must run.
                lock (_gate) _stuckMessage = null;
            }

            _stop.Token.WaitHandle.WaitOne(_interval);
        }
    }

    private void Beat()
    {
        Interlocked.Exchange(ref _lastBeatTicks, Environment.TickCount64);
        Interlocked.Exchange(ref _beatPending, 0);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _thread.Join();
        _stop.Dispose();
    }
}

/// <summary>The UI thread stopped running jobs; the message says what it was doing and which tests wait on it.</summary>
public sealed class UiThreadStuckException(string message) : Exception(message);
