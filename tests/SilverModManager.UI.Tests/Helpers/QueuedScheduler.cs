using System.Reactive.Concurrency;

namespace SilverModManager.UI.Tests.Helpers;

/// <summary>Holds what is scheduled until the test runs it, like a UI thread that has not got round to it.</summary>
public sealed class QueuedScheduler : IScheduler
{
    public Queue<Action> Queue { get; } = new();

    public DateTimeOffset Now => DateTimeOffset.UtcNow;

    public IDisposable Schedule<TState>(TState state, Func<IScheduler, TState, IDisposable> action)
    {
        lock (Queue) Queue.Enqueue(() => action(this, state));
        return System.Reactive.Disposables.Disposable.Empty;
    }

    public IDisposable Schedule<TState>(TState state, TimeSpan dueTime, Func<IScheduler, TState, IDisposable> action) => Schedule(state, action);

    public IDisposable Schedule<TState>(TState state, DateTimeOffset dueTime, Func<IScheduler, TState, IDisposable> action) => Schedule(state, action);

    /// <summary>Runs what is queued, and what those jobs queue in turn, until nothing is left; returns how many ran.</summary>
    public int RunAll()
    {
        var ran = 0;
        while (true)
        {
            Action job;
            lock (Queue)
            {
                if (Queue.Count == 0) return ran;
                job = Queue.Dequeue();
            }
            job();
            ran++;
        }
    }
}
