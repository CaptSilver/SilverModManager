using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.MnemonicDB;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.MnemonicDB.Abstractions.Query;
using SilverModManager.MnemonicDB.Storage;
using SilverModManager.MnemonicDB.Storage.Abstractions;
using RocksDbBackend = SilverModManager.MnemonicDB.Storage.RocksDbBackend.Backend;

namespace SilverModManager.DataModel.Tests;

/// <summary>
/// A DuckDB query reads the database through RocksDB iterators. Closing the native database while one is still
/// open, or opening one afterwards, runs native code on freed memory and kills the process (SIGSEGV), so the
/// backend has to wait for the open iterators and refuse new ones once it is closing.
/// </summary>
public class BackendDisposeTests
{
    private static ServiceProvider BuildProvider() => new ServiceCollection()
        .AddLogging()
        .AddMnemonicDB()
        .AddDatomStoreSettings(new DatomStoreSettings())
        .AddSingleton<IStoreBackend, RocksDbBackend>()
        .BuildServiceProvider();

    [Fact]
    public async Task DisposeWaitsForAnIteratorThatIsStillOpen()
    {
        await using var provider = BuildProvider();
        var connection = provider.GetRequiredService<IConnection>();
        var backend = provider.GetRequiredService<IStoreBackend>();

        var segment = connection.Db.LightweightDatoms(SliceDescriptor.AllEntities(PartitionId.Entity), totalOrdered: true);
        segment.MoveNext(); // a scan that is part way through, as a query between two chunks is

        var dispose = Task.Run(backend.Dispose);
        (await Task.WhenAny(dispose, Task.Delay(500))).Should().NotBeSameAs(dispose,
            "Dispose returned while a query's iterator was still reading the native database");

        segment.Dispose();
        await dispose.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task AnIteratorOpenedAfterDisposeIsRefusedInsteadOfTouchingFreedMemory()
    {
        await using var provider = BuildProvider();
        var connection = provider.GetRequiredService<IConnection>();
        var backend = provider.GetRequiredService<IStoreBackend>();
        var db = connection.Db;

        backend.Dispose();

        var open = () => db.LightweightDatoms(SliceDescriptor.AllEntities(PartitionId.Entity), totalOrdered: true);
        open.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public async Task AQueryRunningAgainstTheConnectionWhileTheProviderIsDisposedEndsWithAnErrorNotACrash()
    {
        var provider = BuildProvider();
        var connection = provider.GetRequiredService<IConnection>();
        await connection.BeginTransaction().Commit();

        using var started = new ManualResetEventSlim();
        var stop = false;
        var failure = default(Exception);
        var worker = Task.Run(() =>
        {
            try
            {
                while (!Volatile.Read(ref stop))
                {
                    connection.Query<long>($"SELECT count(*) FROM mdb_Datoms()").ToList();
                    started.Set();
                }
            }
            catch (Exception e) { failure = e; }
        });

        started.Wait(TimeSpan.FromSeconds(30)).Should().BeTrue();
        await provider.DisposeAsync();
        Volatile.Write(ref stop, true);
        await worker.WaitAsync(TimeSpan.FromSeconds(30));
        // Reaching this line is the assertion: a query on freed native memory never returns.
        failure?.Should().BeAssignableTo<Exception>();
    }
}
