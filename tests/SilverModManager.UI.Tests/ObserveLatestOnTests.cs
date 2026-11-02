using System.Reactive.Concurrency;
using System.Reactive.Subjects;
using FluentAssertions;
using SilverModManager.App.UI;

namespace SilverModManager.UI.Tests;

public class ObserveLatestOnTests
{
    /// <summary>Holds what is scheduled until the test runs it, like a UI thread that has not got round to it.</summary>
    private sealed class QueuedScheduler : IScheduler
    {
        public Queue<Action> Queue { get; } = new();

        public DateTimeOffset Now => DateTimeOffset.UtcNow;

        public IDisposable Schedule<TState>(TState state, Func<IScheduler, TState, IDisposable> action)
        {
            Queue.Enqueue(() => action(this, state));
            return System.Reactive.Disposables.Disposable.Empty;
        }

        public IDisposable Schedule<TState>(TState state, TimeSpan dueTime, Func<IScheduler, TState, IDisposable> action) => Schedule(state, action);

        public IDisposable Schedule<TState>(TState state, DateTimeOffset dueTime, Func<IScheduler, TState, IDisposable> action) => Schedule(state, action);

        public void RunAll()
        {
            while (Queue.Count > 0) Queue.Dequeue()();
        }
    }

    [Fact]
    public void AFloodOfValuesQueuesOneJobAndTheObserverGetsTheLastOfThem()
    {
        var scheduler = new QueuedScheduler();
        var source = new Subject<int>();
        var seen = new List<int>();
        using var subscription = source.ObserveLatestOn(scheduler).Subscribe(seen.Add);

        for (var i = 1; i <= 1000; i++) source.OnNext(i);

        scheduler.Queue.Should().HaveCount(1);
        scheduler.RunAll();
        seen.Should().Equal(1000);
    }

    [Fact]
    public void AValueAfterTheJobRanQueuesTheNextJob()
    {
        var scheduler = new QueuedScheduler();
        var source = new Subject<int>();
        var seen = new List<int>();
        using var subscription = source.ObserveLatestOn(scheduler).Subscribe(seen.Add);

        source.OnNext(1);
        scheduler.RunAll();
        source.OnNext(2);
        source.OnNext(3);
        scheduler.RunAll();

        seen.Should().Equal(1, 3);
    }

    [Fact]
    public void CompletionComesAfterTheLastValue()
    {
        var scheduler = new QueuedScheduler();
        var source = new Subject<int>();
        var seen = new List<string>();
        using var subscription = source.ObserveLatestOn(scheduler).Subscribe(v => seen.Add($"{v}"), () => seen.Add("done"));

        source.OnNext(7);
        source.OnCompleted();
        scheduler.RunAll();

        seen.Should().Equal("7", "done");
    }

    [Fact]
    public void AFailureComesAfterTheLastValue()
    {
        var scheduler = new QueuedScheduler();
        var source = new Subject<int>();
        var seen = new List<string>();
        using var subscription = source.ObserveLatestOn(scheduler).Subscribe(v => seen.Add($"{v}"), e => seen.Add(e.Message));

        source.OnNext(7);
        source.OnError(new InvalidOperationException("the run failed"));
        scheduler.RunAll();

        seen.Should().Equal("7", "the run failed");
    }

    [Fact]
    public void ADisposedSubscriptionGetsNothingMore()
    {
        var scheduler = new QueuedScheduler();
        var source = new Subject<int>();
        var seen = new List<int>();
        var subscription = source.ObserveLatestOn(scheduler).Subscribe(seen.Add);
        source.OnNext(1);

        subscription.Dispose();
        scheduler.RunAll();

        seen.Should().BeEmpty();
    }
}
