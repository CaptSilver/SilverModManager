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
/// A DuckDB query reads the database through RocksDB iterators, from a pool thread. Closing the native database
/// while such a call is inside it, or calling into it afterwards, runs native code on freed memory and kills the
/// process (SIGSEGV). The backend waits for the calls in progress and refuses new ones once it is closing.
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
    public async Task DisposeWaitsForANativeCallThatIsStillInProgress()
    {
        await using var provider = BuildProvider();
        var backend = (RocksDbBackend)provider.GetRequiredService<IStoreBackend>();

        var lease = backend.BeginUse();
        var dispose = Task.Run(backend.Dispose);
        (await Task.WhenAny(dispose, Task.Delay(500))).Should().NotBeSameAs(dispose,
            "Dispose returned while a native call was still inside the database");

        lease.Dispose();
        await dispose.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task ANativeCallAfterDisposeIsRefused()
    {
        await using var provider = BuildProvider();
        var backend = (RocksDbBackend)provider.GetRequiredService<IStoreBackend>();
        backend.Dispose();

        var begin = () => backend.BeginUse();
        begin.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public async Task AScanThatResumesAfterDisposeFailsInsteadOfReadingFreedMemory()
    {
        await using var provider = BuildProvider();
        var connection = provider.GetRequiredService<IConnection>();
        var backend = provider.GetRequiredService<IStoreBackend>();

        var segment = connection.Db.LightweightDatoms(SliceDescriptor.AllEntities(PartitionId.Entity), totalOrdered: true);
        segment.MoveNext(); // part way through, as a query between two chunks is

        var dispose = Task.Run(backend.Dispose);
        await dispose.WaitAsync(TimeSpan.FromSeconds(30));

        var resume = () => segment.MoveNext();
        resume.Should().Throw<ObjectDisposedException>();
        segment.Dispose(); // must not run native code either
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

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task AFinishedQueryDoesNotHoldTheDatabaseOpen(int shape)
    {
        await using var provider = BuildProvider();
        var connection = provider.GetRequiredService<IConnection>();
        var backend = provider.GetRequiredService<IStoreBackend>();
        await connection.BeginTransaction().Commit();

        var rows = shape switch
        {
            0 => connection.Query<long>($"SELECT count(*) FROM mdb_Datoms()").ToList(),
            1 => connection.Query<ulong>($"SELECT E FROM mdb_Datoms() LIMIT 1").ToList().Select(x => (long)x).ToList(),
            _ => connection.Query<ulong>($"SELECT E FROM mdb_Datoms() WHERE E > 0 LIMIT 3").ToList().Select(x => (long)x).ToList(),
        };

        var dispose = Task.Run(backend.Dispose);
        (await Task.WhenAny(dispose, Task.Delay(5000))).Should().BeSameAs(dispose,
            $"a finished query leaves no iterator behind for Dispose to wait for ({rows.Count} rows)");
    }
}
