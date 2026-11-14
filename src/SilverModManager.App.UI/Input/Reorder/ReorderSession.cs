using Avalonia.Threading;
using R3;

namespace SilverModManager.App.UI.Input.Reorder;

/// <summary>
/// Lifts one row and moves it as the pad nudges it. Nudges are cheap and instant; the page is only asked to move the
/// row once presses stop for the settle time (or on drop), so a held D-pad is one move, not a stream of writes. One
/// move runs at a time: a nudge that lands while a commit is in flight is picked up when that commit finishes.
/// </summary>
public sealed class ReorderSession<TKey> : IReorderSession, IDisposable where TKey : notnull
{
    private readonly IReorderable<TKey> _target;
    private readonly TimeProvider _time;
    private readonly TimeSpan _settle;
    private readonly Action<Action> _post;
    private readonly Subject<int> _targetChanged = new();
    private readonly Subject<ReorderFailure> _failed = new();
    private readonly CancellationTokenSource _cancellation = new();
    private ITimer? _timer;
    private Lifted? _lifted;
    private int _committed;
    private bool _cancelling;
    private Task _running = Task.CompletedTask;

    /// <param name="post">
    /// Hands the settle timer's callback to the thread that owns the page. The timer fires on a pool thread; the
    /// session, the page and the row visuals all live on the UI thread.
    /// </param>
    public ReorderSession(IReorderable<TKey> target, TimeProvider time, TimeSpan? settle = null, Action<Action>? post = null)
    {
        _target = target;
        _time = time;
        _settle = settle ?? TimeSpan.FromMilliseconds(250);
        _post = post ?? (action => Dispatcher.UIThread.Post(action));
    }

    // The row travels in a record so an unstarted session holds no key, without a default value standing in for one.
    private sealed record Lifted(TKey Item);

    public bool IsLive { get; private set; }

    public int TargetIndex { get; private set; }

    public int StartIndex { get; private set; }

    public Observable<int> TargetChanged => _targetChanged;

    public Observable<ReorderFailure> Failed => _failed;

    /// <summary>Completes when no commit is running; for a caller that needs the settled state (tests, shutdown).</summary>
    public Task Idle => _running;

    public void Start(TKey item)
    {
        if (IsLive) return;

        _lifted = new Lifted(item);
        StartIndex = _committed = _target.IndexOf(item);
        SetTarget(StartIndex);
        _cancelling = false;
        IsLive = true;
    }

    public void Nudge(int delta)
    {
        if (!IsLive || _lifted is not { } lifted) return;

        SetTarget(Math.Clamp(TargetIndex + delta, 0, Math.Max(0, _target.CountFor(lifted.Item) - 1)));
        _timer?.Dispose();
        _timer = _time.CreateTimer(_ => _post(StartCommit), null, _settle, Timeout.InfiniteTimeSpan);
    }

    public async Task Drop()
    {
        if (!IsLive) return;

        StopTimer();
        await Commit();
        IsLive = false;
    }

    public async Task Cancel()
    {
        if (!IsLive) return;

        StopTimer();
        // What is already in flight finishes first; going back is then one move from wherever it landed.
        await _running;
        if (!IsLive) return;

        _cancelling = true;
        SetTarget(StartIndex);
        await Commit();
        IsLive = false;
    }

    public void Dispose()
    {
        StopTimer();
        _cancellation.Cancel();
        _cancellation.Dispose();
        _targetChanged.Dispose();
        _failed.Dispose();
    }

    private void SetTarget(int index)
    {
        TargetIndex = index;
        _targetChanged.OnNext(index);
    }

    private void StopTimer()
    {
        _timer?.Dispose();
        _timer = null;
    }

    private void StartCommit() => _ = Commit();

    private Task Commit()
    {
        if (!_running.IsCompleted) return _running;

        return _running = Pump();
    }

    private async Task Pump()
    {
        if (_lifted is not { } lifted) return;

        // A nudge that arrives while a move is awaited is picked up by the next pass round the loop.
        while (IsLive && TargetIndex != _committed)
        {
            var delta = TargetIndex - _committed;
            try
            {
                await _target.MoveBy(lifted.Item, delta, _cancellation.Token);
                _committed += delta;
            }
            catch (Exception e) when (IsLive)
            {
                Fail(lifted, e);
                return;
            }
        }
    }

    private void Fail(Lifted lifted, Exception error)
    {
        IsLive = false;
        StopTimer();
        var actual = _target.IndexOf(lifted.Item);
        _committed = actual;
        SetTarget(actual);
        _failed.OnNext(new ReorderFailure(actual, _cancelling, error));
    }
}
