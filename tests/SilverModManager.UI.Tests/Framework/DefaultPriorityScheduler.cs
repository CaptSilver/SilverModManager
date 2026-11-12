using System.Reactive.Concurrency;
using Avalonia.Threading;

namespace SilverModManager.UI.Tests.Framework;

/// <summary>
/// The test host's <c>RxApp.MainThreadScheduler</c>. The app's scheduler posts values that come from another thread at
/// Background priority, below every job the tests' own awaits and the R3 hops post (Default). All UI tests share one
/// dispatcher, so while other tests keep it busy a value handed to <c>OnUI()</c> waits out the whole burst, tens of
/// seconds on a loaded run, and the page a test polls stays empty. Here such a value is posted at Default, so it takes
/// its turn in the queue with the rest. On the UI thread, and for timed work, it behaves like the app's scheduler.
/// </summary>
public sealed class DefaultPriorityScheduler(IScheduler inner) : IScheduler
{
    public DateTimeOffset Now => inner.Now;

    public IDisposable Schedule<TState>(TState state, Func<IScheduler, TState, IDisposable> action)
    {
        if (Dispatcher.UIThread.CheckAccess()) return inner.Schedule(state, (_, s) => action(this, s));

        var composite = new System.Reactive.Disposables.CompositeDisposable(2);
        var cancellation = new System.Reactive.Disposables.CancellationDisposable();
        Dispatcher.UIThread.Post(() =>
        {
            if (!cancellation.Token.IsCancellationRequested) composite.Add(action(this, state));
        }, DispatcherPriority.Default);
        composite.Add(cancellation);
        return composite;
    }

    public IDisposable Schedule<TState>(TState state, TimeSpan dueTime, Func<IScheduler, TState, IDisposable> action) =>
        inner.Schedule(state, dueTime, (_, s) => action(this, s));

    public IDisposable Schedule<TState>(TState state, DateTimeOffset dueTime, Func<IScheduler, TState, IDisposable> action) =>
        inner.Schedule(state, dueTime, (_, s) => action(this, s));
}
