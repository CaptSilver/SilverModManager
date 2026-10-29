using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
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

    private static ServiceProvider BuildProvider() => new ServiceCollection()
        .AddLogging()
        .AddMnemonicDB()
        .AddDatomStoreSettings(new DatomStoreSettings())
        .AddSingleton<IStoreBackend, RocksDbBackend>()
        .BuildServiceProvider();
}
