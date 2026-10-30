using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.MnemonicDB;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Storage;
using SilverModManager.MnemonicDB.Storage.Abstractions;
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
        .BuildServiceProvider();

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

    [Fact]
    public async Task DisposeLeavesDuckDbOpenWhenAQueryNeverFinishes()
    {
        await using var provider = BuildProvider();
        var engine = (QueryEngine)provider.GetRequiredService<IQueryEngine>();
        engine.QueryDrainWait = TimeSpan.FromMilliseconds(200);

        var stuck = engine.DuckDb.Connect();
        await engine.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));

        // Closing it under the query would crash the process; the engine leaves it open instead.
        using var another = engine.DuckDb.Connect();
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
