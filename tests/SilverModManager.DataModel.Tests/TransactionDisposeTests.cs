using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.MnemonicDB;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.MnemonicDB.Storage;
using SilverModManager.MnemonicDB.Storage.Abstractions;
using RocksDbBackend = SilverModManager.MnemonicDB.Storage.RocksDbBackend.Backend;

namespace SilverModManager.DataModel.Tests;

/// <summary>
/// Commit hands the store thread the transaction's functions and returns a task. A caller that disposes the
/// transaction before that task completes must not take the functions away from the store thread.
/// </summary>
public class TransactionDisposeTests
{
    private static ServiceProvider CreateProvider() => new ServiceCollection()
        .AddLogging()
        .AddMnemonicDB()
        .AddDatomStoreSettings(new DatomStoreSettings())
        .AddSingleton<IStoreBackend, RocksDbBackend>()
        .BuildServiceProvider();

    [Fact]
    public async Task DisposingAfterCommitReturns_StillRunsTheTransactionsFunctions()
    {
        await using var provider = CreateProvider();
        var connection = provider.GetRequiredService<IConnection>();
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        // Park the store thread inside this transaction, so the next one is still queued when it is disposed.
        using var blocker = connection.BeginTransaction();
        blocker.Add((started, release), static (_, _, gate) =>
        {
            gate.started.Set();
            gate.release.Wait(TimeSpan.FromSeconds(30));
        });
        var blocking = blocker.Commit();
        started.Wait(TimeSpan.FromSeconds(30)).Should().BeTrue("the store thread has to be parked before the next commit queues");

        var ran = new StrongBox<bool>();
        var tx = connection.BeginTransaction();
        tx.Add(ran, static (_, _, box) => box.Value = true);
        var committing = tx.Commit();
        tx.Dispose();

        release.Set();
        await blocking;
        await committing;

        ran.Value.Should().BeTrue("the function was part of the commit that was already queued");
    }
}
