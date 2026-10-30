using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.MnemonicDB;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.MnemonicDB.Storage;
using SilverModManager.MnemonicDB.Storage.Abstractions;
using SilverModManager.Sdk.Games;
using RocksDbBackend = SilverModManager.MnemonicDB.Storage.RocksDbBackend.Backend;

namespace SilverModManager.DataModel.Tests;

/// <summary>
/// Closing DuckDB destroys every pooled connection, including one a running query has checked out, and the query then
/// locks a freed context (SIGSEGV). The engine waits for the queries in flight before it closes DuckDB.
/// </summary>
public class QueryEngineDisposeTests
{
    private static ServiceProvider BuildProvider() => new ServiceCollection()
        .AddLogging()
        .AddMnemonicDB()
        .AddDatomStoreSettings(new DatomStoreSettings())
        .AddSingleton<IStoreBackend, RocksDbBackend>()
        .AddGameInstallMetadataModel()
        .BuildServiceProvider();

    // A commit that adds a datom, which bumps the revision of the model table function that the live query depends on.
    private static async Task CommitADatom(IConnection connection, string name)
    {
        using var tx = connection.BeginTransaction();
        tx.Add(tx.TempId(), GameInstallMetadata.Name, name);
        await tx.Commit();
    }

    [Fact]
    public async Task DisposeWaitsForAQueryThatIsStillRunning()
    {
        await using var provider = BuildProvider();
        var engine = (QueryEngine)provider.GetRequiredService<IQueryEngine>();

        var running = engine.DuckDb.Connect(); // a query holds its connection for as long as it runs
        var dispose = engine.DisposeAsync().AsTask();
        (await Task.WhenAny(dispose, Task.Delay(500))).Should().NotBeSameAs(dispose,
            "DuckDB was closed while a query still held a connection");

        running.Dispose();
        await dispose.WaitAsync(TimeSpan.FromSeconds(30));

        var connect = () => engine.DuckDb.Connect();
        connect.Should().Throw<ObjectDisposedException>("DuckDB is closed once the queries have finished");
    }

    // Without this a steady stream of queries never leaves a moment with none running, and the wait never ends.
    [Fact]
    public async Task AQueryThatStartsWhileDisposeIsWaitingIsRefused()
    {
        await using var provider = BuildProvider();
        var engine = (QueryEngine)provider.GetRequiredService<IQueryEngine>();

        var running = engine.DuckDb.Connect();
        var dispose = engine.DisposeAsync().AsTask();
        await Task.Delay(100);

        var connect = () => engine.DuckDb.Connect();
        connect.Should().Throw<ObjectDisposedException>("new queries must not keep the running count from reaching zero");

        running.Dispose();
        await dispose.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task DisposeLeavesDuckDbOpenWhenAQueryNeverFinishes()
    {
        await using var provider = BuildProvider();
        var engine = (QueryEngine)provider.GetRequiredService<IQueryEngine>();
        engine.QueryDrainWait = TimeSpan.FromMilliseconds(200);

        var stuck = engine.DuckDb.Connect();
        await engine.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));

        // Closing it under the query would crash the process; the engine leaves it open, and the query's own
        // connection still works.
        stuck.Query("SELECT 1").Dispose();
        stuck.Dispose();
    }

    [Fact]
    public async Task DisposeWithNoQueryRunningClosesDuckDb()
    {
        await using var provider = BuildProvider();
        var engine = (QueryEngine)provider.GetRequiredService<IQueryEngine>();
        using (var idle = engine.DuckDb.Connect()) { } // returned to the pool, not in use

        await engine.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));

        var connect = () => engine.DuckDb.Connect();
        connect.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public async Task TheEngineCanSeeHowManyConnectionsAreInUse()
    {
        // The count comes from DuckDB's private pools, which the pinned HyperDuck version gives no public way to read.
        // A package update that renames them has to fail here rather than make the engine stop waiting.
        await using var provider = BuildProvider();
        var engine = (QueryEngine)provider.GetRequiredService<IQueryEngine>();

        engine.ConnectionsInUse().Should().Be(0);
        var held = engine.DuckDb.Connect();
        engine.ConnectionsInUse().Should().Be(1);
        held.Dispose();
        engine.ConnectionsInUse().Should().Be(0);
    }

    // HyperDuck's live-query updater pulses every 250 ms and treats only a cancelled delay as a clean stop. If it is
    // still running while the engine refuses queries, the pulse throws ObjectDisposedException, the updater task
    // faults, and the later DuckDB.DisposeAsync rethrows it without closing anything.
    [Fact]
    public async Task DisposeWithALiveQueryRegisteredStopsTheUpdaterBeforeRefusingQueries()
    {
        await using var provider = BuildProvider();
        var engine = (QueryEngine)provider.GetRequiredService<IQueryEngine>();
        var connection = provider.GetRequiredService<IConnection>();
        await CommitADatom(connection, "before");

        using var subscription = connection
            .Query<EntityId>($"SELECT Id FROM mdb_GameInstallMetadata(Db=>{connection})")
            .Observe(x => x)
            .Subscribe(_ => { });
        await Task.Delay(600); // let the updater run a few pulses

        var running = engine.DuckDb.Connect();
        var dispose = engine.DisposeAsync().AsTask();
        await Task.Delay(100);

        // Changes the revision the live query depends on, so the next pulse would re-run it if the updater were alive.
        await CommitADatom(connection, "during the drain");
        await Task.Delay(800); // several pulses

        running.Dispose();
        await dispose.WaitAsync(TimeSpan.FromSeconds(30)); // rethrows if the updater faulted

        var connect = () => engine.DuckDb.Connect();
        connect.Should().Throw<ObjectDisposedException>();
    }

    // The updater's pulse fails once the connection it queries is gone, and the container disposes the connection before
    // the engine. The failure must not stop DuckDB from closing.
    [Fact]
    public async Task DisposeStillClosesDuckDbWhenALiveQueryUpdateAlreadyFailed()
    {
        await using var provider = BuildProvider();
        var engine = (QueryEngine)provider.GetRequiredService<IQueryEngine>();
        var connection = provider.GetRequiredService<IConnection>();
        await CommitADatom(connection, "before");
        using var subscription = connection
            .Query<EntityId>($"SELECT Id FROM mdb_GameInstallMetadata(Db=>{connection})")
            .Observe(x => x)
            .Subscribe(_ => { });

        engine.LiveQueryUpdaterIfStarted()!.Add(new FailingLiveQuery());
        await Task.Delay(600); // the next pulse throws and faults the updater task

        await engine.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));

        var connect = () => engine.DuckDb.Connect();
        connect.Should().Throw<ObjectDisposedException>("DuckDB closed despite the faulted updater");
    }

    private sealed class FailingLiveQuery : NexusMods.HyperDuck.Internals.ILiveQuery
    {
        public ulong Id { get; } = NexusMods.HyperDuck.Internals.ILiveQuery.NextId();
        public void Update() => throw new InvalidOperationException("live query failure injected by the test");
        public void Dispose() { }
    }

    [Fact]
    public async Task TheEngineCanFindHyperDucksLiveQueryUpdater()
    {
        // The updater hangs off a private member of DuckDB; a package update that renames it has to fail here.
        await using var provider = BuildProvider();
        var engine = (QueryEngine)provider.GetRequiredService<IQueryEngine>();

        engine.LiveQueryUpdaterIfStarted().Should().BeNull("nothing has registered a live query yet");
        var connection = provider.GetRequiredService<IConnection>();
        using var subscription = connection.Query<EntityId>($"SELECT Id FROM mdb_GameInstallMetadata(Db=>{connection})").Observe(x => x).Subscribe(_ => { });
        engine.LiveQueryUpdaterIfStarted().Should().NotBeNull();
    }

    // The shape that killed the process before the dispose test was narrowed to the backend: queries running while
    // the whole container goes away.
    [Fact]
    public async Task QueriesRunningWhileTheProviderIsDisposedEndWithAnErrorNotACrash()
    {
        var provider = BuildProvider();
        var connection = provider.GetRequiredService<IConnection>();
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
        await provider.DisposeAsync();

        await worker.WaitAsync(TimeSpan.FromSeconds(30));
        failure.Should().NotBeNull("the loop runs until a query fails, which it does once DuckDB is closed");
    }
}
