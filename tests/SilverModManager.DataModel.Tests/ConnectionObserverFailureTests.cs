using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.MnemonicDB;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.BuiltInEntities;
using SilverModManager.MnemonicDB.Abstractions.Query;
using SilverModManager.MnemonicDB.Storage;
using SilverModManager.MnemonicDB.Storage.Abstractions;
using RocksDbBackend = SilverModManager.MnemonicDB.Storage.RocksDbBackend.Backend;

namespace SilverModManager.DataModel.Tests;

/// <summary>
/// Subscribers to a connection run on its event thread while the commit that produced the revision waits for
/// them. One that throws must not leave that commit, and every later one, waiting for good.
/// </summary>
public class ConnectionObserverFailureTests
{
    [Fact]
    public async Task ARevisionSubscriberThatThrowsDoesNotStallTheCommit()
    {
        await using var provider = BuildProvider();
        var connection = provider.GetRequiredService<IConnection>();

        var armed = false;
        using var subscription = connection.Revisions.Subscribe(_ =>
        {
            if (armed) throw new InvalidOperationException("the subscriber failed");
        }, _ => { });
        armed = true;

        await connection.BeginTransaction().Commit().WaitAsync(TimeSpan.FromSeconds(15));

        // The connection still takes the next transaction.
        await connection.BeginTransaction().Commit().WaitAsync(TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// New observers are primed in batches on the event thread. One whose first emission throws must not drop the
    /// observers queued behind it in the same batch: they would never get their initial snapshot.
    /// </summary>
    [Fact]
    public async Task AnObserverThatThrowsWhilePrimedDoesNotDropTheOnesQueuedBehindIt()
    {
        await using var provider = BuildProvider();
        var connection = provider.GetRequiredService<IConnection>();

        // Holding the event thread inside the first observer's priming lets the next two queue up as one batch.
        using var gate = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        using var blocker = connection.ObserveDatoms(AttributeDefinition.UniqueId).Subscribe(_ =>
        {
            entered.Set();
            gate.Wait();
        });
        entered.Wait(TimeSpan.FromSeconds(15)).Should().BeTrue("the first observer is primed on the event thread");

        using var thrower = connection.ObserveDatoms(AttributeDefinition.UniqueId)
            .Subscribe(_ => throw new InvalidOperationException("the observer failed"), _ => { });
        var primed = new TaskCompletionSource();
        using var healthy = connection.ObserveDatoms(AttributeDefinition.UniqueId).Subscribe(_ => primed.TrySetResult());

        gate.Set();

        await primed.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// Past the sequential limit a batch is primed with Parallel.For; a throwing observer in it must not stop the rest.
    /// </summary>
    [Fact]
    public async Task AnObserverThatThrowsWhilePrimedDoesNotDropTheOnesQueuedBehindItInALargeBatch()
    {
        await using var provider = BuildProvider();
        var connection = provider.GetRequiredService<IConnection>();

        using var gate = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        using var blocker = connection.ObserveDatoms(AttributeDefinition.UniqueId).Subscribe(_ =>
        {
            entered.Set();
            gate.Wait();
        });
        entered.Wait(TimeSpan.FromSeconds(15)).Should().BeTrue("the first observer is primed on the event thread");

        const int healthyCount = 40;
        var primed = new CountdownEvent(healthyCount);
        var subscriptions = new List<IDisposable>();
        for (var i = 0; i < healthyCount; i++)
        {
            if (i == 7) subscriptions.Add(connection.ObserveDatoms(AttributeDefinition.UniqueId).Subscribe(_ => throw new InvalidOperationException("the observer failed"), _ => { }));
            subscriptions.Add(connection.ObserveDatoms(AttributeDefinition.UniqueId).Subscribe(_ => primed.Signal()));
        }

        gate.Set();

        try
        {
            var allPrimed = await Task.Run(() => primed.Wait(TimeSpan.FromSeconds(15)));
            allPrimed.Should().BeTrue("every healthy observer in the batch is primed");
        }
        finally
        {
            foreach (var subscription in subscriptions) subscription.Dispose();
            primed.Dispose();
        }
    }

    /// <summary>
    /// The dropped observer gets no error (its downstream has already stopped), so the log line is the only trace
    /// of the subscription. It has to say which slice it was watching.
    /// </summary>
    [Fact]
    public async Task TheLogNamesTheSliceOfAnObserverThatFailedWhilePrimed()
    {
        var messages = new List<string>();
        await using var provider = BuildProvider(messages);
        var connection = provider.GetRequiredService<IConnection>();

        var failed = new TaskCompletionSource();
        using var thrower = connection.ObserveDatoms(AttributeDefinition.UniqueId)
            .Subscribe(_ =>
            {
                failed.TrySetResult();
                throw new InvalidOperationException("the observer failed");
            }, _ => { });
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(15));

        // The log call follows the throw on the event thread; a later commit goes through the same thread.
        await connection.BeginTransaction().Commit().WaitAsync(TimeSpan.FromSeconds(15));

        string[] logged;
        lock (messages) logged = messages.ToArray();
        logged.Should().ContainSingle(static m => m.Contains("failed while being primed"))
            .Which.Should().Contain("UniqueId", "the line has to identify the subscription");
    }

    private static ServiceProvider BuildProvider(List<string>? messages = null) => new ServiceCollection()
        .AddLogging(builder =>
        {
            if (messages is not null) builder.AddProvider(new ListLoggerProvider(messages));
        })
        .AddMnemonicDB()
        .AddDatomStoreSettings(new DatomStoreSettings())
        .AddSingleton<IStoreBackend, RocksDbBackend>()
        .BuildServiceProvider();

    private sealed class ListLoggerProvider(List<string> messages) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new ListLogger(messages);
        public void Dispose() { }

        private sealed class ListLogger(List<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (messages) messages.Add(formatter(state, exception));
            }
        }
    }
}
