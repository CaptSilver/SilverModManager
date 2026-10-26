using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.MnemonicDB;
using SilverModManager.MnemonicDB.Abstractions;
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
        await using var provider = new ServiceCollection()
            .AddLogging()
            .AddMnemonicDB()
            .AddDatomStoreSettings(new DatomStoreSettings())
            .AddSingleton<IStoreBackend, RocksDbBackend>()
            .BuildServiceProvider();
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
}
