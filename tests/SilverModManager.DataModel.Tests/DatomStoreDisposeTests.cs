using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.MnemonicDB;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.Internals;
using SilverModManager.MnemonicDB.Storage;
using SilverModManager.MnemonicDB.Storage.Abstractions;
using RocksDbBackend = SilverModManager.MnemonicDB.Storage.RocksDbBackend.Backend;

namespace SilverModManager.DataModel.Tests;

/// <summary>
/// The store's transaction thread talks to RocksDB, and the backend frees the native database when the
/// container disposes it right after the store. A store that returns from Dispose while that thread is
/// still inside the backend leaves the thread using freed native memory, which kills the whole process
/// (SIGSEGV in rocksdb_create_snapshot, or "pthread lock: Invalid argument" and SIGABRT).
/// </summary>
public class DatomStoreDisposeTests
{
    [Fact]
    public async Task DisposeWaitsForATransactionThatIsStillInsideTheBackend()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var backend = new BlockingBackend(entered, release);

        var provider = new ServiceCollection()
            .AddLogging()
            .AddMnemonicDB()
            .AddDatomStoreSettings(new DatomStoreSettings())
            .AddSingleton<IStoreBackend>(backend)
            .BuildServiceProvider();
        var store = provider.GetRequiredService<DatomStore>();
        var connection = provider.GetRequiredService<IConnection>();

        backend.Block = true;
        var commit = Task.Run(() =>
        {
            try { connection.BeginTransaction().Commit().GetAwaiter().GetResult(); }
            catch (Exception) { /* the store is shutting down underneath it */ }
        });
        entered.Wait(TimeSpan.FromSeconds(30)).Should().BeTrue("the transaction thread must reach the backend");

        var dispose = Task.Run(store.Dispose);
        (await Task.WhenAny(dispose, Task.Delay(500))).Should().NotBeSameAs(dispose,
            "Dispose returned while the transaction thread was still using the backend");

        release.Set();
        await dispose.WaitAsync(TimeSpan.FromSeconds(30));
        await commit.WaitAsync(TimeSpan.FromSeconds(30));
        backend.Dispose();
    }

    private sealed class BlockingBackend(ManualResetEventSlim entered, ManualResetEventSlim release) : IStoreBackend
    {
        private readonly RocksDbBackend _inner = new();
        public volatile bool Block;

        public AttributeCache AttributeCache => _inner.AttributeCache;
        public void Init(DatomStoreSettings settings) => _inner.Init(settings);
        public ISnapshot GetSnapshot() => _inner.GetSnapshot();
        public void FlushAndCompact(bool verify) => _inner.FlushAndCompact(verify);
        public void Dispose() => _inner.Dispose();

        public IWriteBatch CreateBatch()
        {
            if (Block)
            {
                entered.Set();
                release.Wait();
            }
            return _inner.CreateBatch();
        }
    }
}
