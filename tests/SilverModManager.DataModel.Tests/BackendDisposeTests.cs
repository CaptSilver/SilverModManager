using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.MnemonicDB;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.MnemonicDB.Abstractions.Query;
using SilverModManager.MnemonicDB.Storage;
using SilverModManager.MnemonicDB.Storage.Abstractions;
using RocksDbSharp;
using RocksDbBackend = SilverModManager.MnemonicDB.Storage.RocksDbBackend.Backend;
using RocksDbIteratorWrapper = SilverModManager.MnemonicDB.Storage.RocksDbBackend.RocksDbIteratorWrapper;

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

    // Holds a lease so Dispose parks in its wait, then polls until the backend has started closing.
    private static async Task<(RocksDbBackend.UseLease Lease, Task Dispose)> CloseWhileALeaseIsHeld(RocksDbBackend backend)
    {
        var lease = backend.BeginUse();
        var dispose = Task.Run(backend.Dispose);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            try { backend.BeginUse().Dispose(); }
            catch (ObjectDisposedException) { return (lease, dispose); }
            await Task.Delay(10);
        }
        throw new TimeoutException("Dispose never started closing the backend");
    }

    [Fact]
    public async Task ABatchCommittedWhileTheBackendIsClosingIsRefused()
    {
        await using var provider = BuildProvider();
        provider.GetRequiredService<IConnection>(); // opens the database
        var backend = (RocksDbBackend)provider.GetRequiredService<IStoreBackend>();
        using var batch = backend.CreateBatch();

        var (lease, dispose) = await CloseWhileALeaseIsHeld(backend);
        try
        {
            var commit = () => batch.Commit();
            commit.Should().Throw<ObjectDisposedException>("a write must not start on a database that is being closed");
        }
        finally
        {
            lease.Dispose();
            await dispose.WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    [Fact]
    public async Task AFlushAndCompactWhileTheBackendIsClosingIsRefused()
    {
        await using var provider = BuildProvider();
        provider.GetRequiredService<IConnection>(); // opens the database
        var backend = (RocksDbBackend)provider.GetRequiredService<IStoreBackend>();

        var (lease, dispose) = await CloseWhileALeaseIsHeld(backend);
        try
        {
            var flush = () => backend.FlushAndCompact(verify: true);
            flush.Should().Throw<ObjectDisposedException>("a compaction must not start on a database that is being closed");
        }
        finally
        {
            lease.Dispose();
            await dispose.WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    // Nothing reads RocksDB's periodic statistics dumps, and the timer thread that writes them is one more native
    // thread running inside the database while it closes.
    [Fact]
    public async Task TheDatabaseDoesNotDumpStatisticsOnATimer()
    {
        await using var provider = BuildProvider();
        provider.GetRequiredService<IConnection>(); // opens the database
        var backend = (RocksDbBackend)provider.GetRequiredService<IStoreBackend>();

        var options = backend.Options;
        options.Should().NotBeNull();
        Native.Instance.rocksdb_options_get_stats_dump_period_sec(options!.Handle).Should().Be(0);
        Native.Instance.rocksdb_options_get_stats_persist_period_sec(options.Handle).Should().Be(0);
    }

    // A database still open when the process calls native exit() leaves RocksDB's background threads running into
    // the exit teardown. The process-exit hook closes what is left; these tests drive the same routine on backends
    // they own, so they cannot close a database another test in the process is using.
    private static RocksDbBackend OpenInMemoryBackend()
    {
        var backend = new RocksDbBackend();
        backend.Init(DatomStoreSettings.InMemory);
        return backend;
    }

    [Fact]
    public void ABackendIsListedAsOpenFromInitUntilItIsClosed()
    {
        var backend = OpenInMemoryBackend();
        RocksDbBackend.OpenBackends().Should().Contain(backend);

        backend.Dispose();
        RocksDbBackend.OpenBackends().Should().NotContain(backend);
    }

    [Fact]
    public void ClosingTheOpenBackendsClosesOneThatWasNeverDisposed()
    {
        var abandoned = OpenInMemoryBackend();

        RocksDbBackend.CloseAll([abandoned], TimeSpan.FromSeconds(10));

        var begin = () => abandoned.BeginUse();
        begin.Should().Throw<ObjectDisposedException>();
        RocksDbBackend.OpenBackends().Should().NotContain(abandoned);
    }

    [Fact]
    public async Task ClosingTheOpenBackendsGivesUpOnABusyOneWithinTheBudgetAndStillClosesTheRest()
    {
        var busy = OpenInMemoryBackend();
        var idle = OpenInMemoryBackend();
        var lease = busy.BeginUse();
        try
        {
            var started = DateTime.UtcNow;
            await Task.Run(() => RocksDbBackend.CloseAll([busy, idle], TimeSpan.FromMilliseconds(300)));

            (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(10), "the exit hook must not stall the process");
            RocksDbBackend.OpenBackends().Should().NotContain(idle);
            RocksDbBackend.OpenBackends().Should().Contain(busy, "a database with a call inside it is not freed");
        }
        finally
        {
            lease.Dispose();
            RocksDbBackend.CloseAll([busy], TimeSpan.FromSeconds(10));
        }
        RocksDbBackend.OpenBackends().Should().NotContain(busy);
    }

    [Fact]
    public void ADisposeThatGivesUpLeavesTheBackendListedSoItCanBeClosedLater()
    {
        var backend = OpenInMemoryBackend();
        backend.UseWait = TimeSpan.FromMilliseconds(100);
        var lease = backend.BeginUse();

        backend.Dispose(); // gives up: a call is still inside the database
        RocksDbBackend.OpenBackends().Should().Contain(backend);

        lease.Dispose();
        RocksDbBackend.CloseAll([backend], TimeSpan.FromSeconds(10));
        RocksDbBackend.OpenBackends().Should().NotContain(backend);
    }

    [Fact]
    public void AnAbandonedBackendIsNotFinalizedWhileItsDatabaseIsOpen()
    {
        var weak = OpenAbandonedBackend();
        for (var i = 0; i < 2; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        weak.TryGetTarget(out var backend).Should().BeTrue("finalizers free the database and its environment in no fixed order");
        RocksDbBackend.CloseAll([backend!], TimeSpan.FromSeconds(10));
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference<RocksDbBackend> OpenAbandonedBackend() => new(OpenInMemoryBackend());

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

    // The lease covers a native call, not what the caller does with the memory the call returned. A key or value read
    // after the lease ended, while the database closes, would be read from the closed database, so the wrapper hands
    // out its own copy. Moving the native iterator on overwrites the memory a pointer into RocksDB would still show.
    [Fact]
    public async Task TheKeyAndValueOfARowAreCopiedOutOfTheDatabaseUnderTheLease()
    {
        await using var provider = BuildProvider();
        var connection = provider.GetRequiredService<IConnection>();
        var backend = (RocksDbBackend)provider.GetRequiredService<IStoreBackend>();
        await connection.BeginTransaction().Commit();

        var native = backend.NewIterator(new ReadOptions());
        var rows = new RocksDbIteratorWrapper(backend, native);
        rows.MoveNext(SliceDescriptor.AllEntities(PartitionId.Attribute)).Should().BeTrue("the store holds the attribute definitions");
        var key = rows.Current;
        var keyBefore = key.Span.ToArray();
        var value = rows.ExtraValueSpan;
        var valueBefore = value.Span.ToArray();

        native.Next();
        native.Next();

        key.Span.ToArray().Should().Equal(keyBefore, "the key must not live in memory the database owns");
        value.Span.ToArray().Should().Equal(valueBefore, "the value must not live in memory the database owns");
        rows.Dispose();
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
    public async Task QueriesRunningWhileTheBackendIsDisposedEndWithAnErrorNotACrash()
    {
        await using var provider = BuildProvider();
        var connection = provider.GetRequiredService<IConnection>();
        var backend = provider.GetRequiredService<IStoreBackend>();
        await connection.BeginTransaction().Commit();

        using var started = new ManualResetEventSlim();
        var failure = default(Exception);
        var worker = Task.Run(() =>
        {
            try
            {
                while (true)
                {
                    connection.Query<long>($"SELECT count(*) FROM mdb_Datoms()").ToList();
                    started.Set();
                }
            }
            catch (Exception e) { failure = e; }
        });

        started.Wait(TimeSpan.FromSeconds(30)).Should().BeTrue();
        backend.Dispose();

        // Reaching the end is the assertion: a scan on freed native memory kills the process, and the loop only
        // ends once a query fails.
        await worker.WaitAsync(TimeSpan.FromSeconds(30));
        failure.Should().NotBeNull("the loop runs until a query fails, which it does once the database is closed");
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
