using System.Reactive;
using System.Reactive.Linq;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.HyperDuck;
using SilverModManager.MnemonicDB;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.Query;
using SilverModManager.MnemonicDB.Storage;
using SilverModManager.MnemonicDB.Storage.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using RocksDbBackend = SilverModManager.MnemonicDB.Storage.RocksDbBackend.Backend;

namespace SilverModManager.DataModel.Tests;

/// <summary>
/// Subscribing from the UI thread must not run the query there: a view model that subscribes on activation would
/// freeze the window for as long as DuckDB takes to answer.
/// </summary>
public class QueryObservableTests
{
    private static ServiceProvider BuildProvider(QueryProbe probe) => new ServiceCollection()
        .AddLogging()
        .AddMnemonicDB()
        .AddDatomStoreSettings(new DatomStoreSettings())
        .AddSingleton<IStoreBackend, RocksDbBackend>()
        .AddGameInstallMetadataModel()
        .AddSingleton(probe)
        .AddSingleton<AScalarFunction, ProbeScalar>()
        .BuildServiceProvider();

    private static IObservable<DynamicData.IChangeSet<long, long>> Observed(IConnection connection) =>
        connection.Query<long>($"SELECT smm_test_probe(CAST(Id AS BIGINT)) FROM mdb_GameInstallMetadata(Db=>{connection})").ObserveInBackground(x => x);

    private static async Task<EntityId> CommitARow(IConnection connection)
    {
        using var tx = connection.BeginTransaction();
        // The table function and the model stream list only complete entities, so a bare Name would give no rows.
        var install = new GameInstallMetadata.New(tx)
        {
            Store = GameStore.Unknown,
            Path = "/games/" + Guid.NewGuid(),
            Name = "probe",
        };
        var result = await tx.Commit();
        return result[install.Id];
    }

    [Fact]
    public async Task SubscribeReturnsWhileTheQueryIsStillRunning()
    {
        var probe = new QueryProbe();
        await using var provider = BuildProvider(probe);
        var connection = provider.GetRequiredService<IConnection>();
        await CommitARow(connection);

        // The query cannot finish until the gate opens, so a Subscribe that runs it never returns.
        probe.Gate.Reset();
        var values = new TaskCompletionSource<long>();
        using var subscriber = SubscribeFromAThread(Observed(connection), changes =>
        {
            foreach (var change in changes) values.TrySetResult(change.Current);
        });

        try
        {
            var returned = await Task.WhenAny(subscriber.Returned, Task.Delay(TimeSpan.FromSeconds(15)));
            returned.Should().BeSameAs(subscriber.Returned, "Subscribe waited for the query to finish");
        }
        finally
        {
            probe.Gate.Set();
        }

        (await values.Task.WaitAsync(TimeSpan.FromSeconds(30))).Should().BeGreaterThan(0, "the first value still arrives");
        await AwaitRegistered(provider);
        probe.Threads.Should().NotBeEmpty("the test query ran");
        probe.Threads.Should().NotContain(subscriber.ThreadId, "the query ran on the subscribing thread");
    }

    [Fact]
    public async Task TheFirstValueArrivesAfterSubscribeHasReturned()
    {
        var probe = new QueryProbe();
        await using var provider = BuildProvider(probe);
        var connection = provider.GetRequiredService<IConnection>();
        await CommitARow(connection);

        // The query is held until Subscribe has returned, and "returned" is noted on the subscribing thread before the
        // hold is lifted, so a first value can only come first when the query ran inside Subscribe.
        probe.Gate.Reset();
        var events = new List<string>();
        var first = new TaskCompletionSource();
        using var subscriber = SubscribeFromAThread(Observed(connection), _ =>
        {
            lock (events) events.Add("value");
            first.TrySetResult();
        }, onReturned: () =>
        {
            lock (events) events.Add("returned");
            probe.Gate.Set();
        });

        await first.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await AwaitRegistered(provider);

        // Later pulses may deliver again; only the order of the first two matters.
        lock (events) events.Take(2).Should().Equal("returned", "value");
        subscriber.ThreadId.Should().NotBe(Environment.CurrentManagedThreadId);
    }

    [Fact]
    public async Task AFailingFirstQueryErrorsTheStreamInsteadOfThrowingFromSubscribe()
    {
        var probe = new QueryProbe { Fail = true };
        await using var provider = BuildProvider(probe);
        var connection = provider.GetRequiredService<IConnection>();
        await CommitARow(connection);

        var error = new TaskCompletionSource<Exception>();
        using var subscriber = SubscribeFromAThread(Observed(connection).Materialize(), notification =>
        {
            if (notification.Kind == NotificationKind.OnError) error.TrySetResult(notification.Exception!);
        });

        await subscriber.Returned.WaitAsync(TimeSpan.FromSeconds(30));
        (await error.Task.WaitAsync(TimeSpan.FromSeconds(30))).Should().NotBeNull();
    }

    [Fact]
    public async Task TheModelObserveAnswersOffTheSubscribersThread()
    {
        var probe = new QueryProbe();
        await using var provider = BuildProvider(probe);
        var connection = provider.GetRequiredService<IConnection>();
        var id = await CommitARow(connection);

        var (subscriber, value) = await FirstValueThreads(GameInstallMetadata.Observe(connection, id));

        value.Should().NotBe(subscriber, "the first value was produced inside Subscribe");
    }

    /// <summary>
    /// The first value is delivered before the live query is registered for updates. Closing DuckDB in that gap
    /// leaves the updater starting against a closed engine, so a test that ends on the first value waits for it.
    /// </summary>
    private static async Task AwaitRegistered(ServiceProvider provider)
    {
        var engine = (QueryEngine)provider.GetRequiredService<IQueryEngine>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (engine.LiveQueryUpdaterIfStarted() is null) await Task.Delay(10, timeout.Token);
        await provider.GetRequiredService<IConnection>().FlushQueries().WaitAsync(TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// A thread that subscribes and then stays alive until disposed. Anything that ran inside Subscribe ran on it, and
    /// because it outlives the assertions no other thread can be handed its managed id meanwhile.
    /// </summary>
    public sealed class Subscriber : IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        private readonly TaskCompletionSource _returned = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Thread _thread;
        private IDisposable? _subscription;

        public Subscriber(Func<IDisposable> subscribe, Action? onReturned)
        {
            var started = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            _thread = new Thread(() =>
            {
                started.SetResult(Environment.CurrentManagedThreadId);
                _subscription = subscribe();
                onReturned?.Invoke();
                _returned.SetResult();
                _release.Wait();
            }) { IsBackground = true };
            _thread.Start();
            ThreadId = started.Task.GetAwaiter().GetResult();
        }

        public int ThreadId { get; }

        /// <summary>Completes once Subscribe has returned on the subscribing thread.</summary>
        public Task Returned => _returned.Task;

        public void Dispose()
        {
            _release.Set();
            _thread.Join(TimeSpan.FromSeconds(30));
            _subscription?.Dispose();
            _release.Dispose();
        }
    }

    public static Subscriber SubscribeFromAThread<T>(IObservable<T> source, Action<T> onNext, Action? onReturned = null) =>
        new(() => source.Subscribe(onNext), onReturned);

    /// <summary>
    /// Subscribes from a thread of its own and reports that thread's id and the id of the thread the first value
    /// arrived on. A value produced inside Subscribe shows up on the subscriber's own thread.
    /// </summary>
    public static async Task<(int Subscriber, int Value)> FirstValueThreads<T>(IObservable<T> source)
    {
        var first = new TaskCompletionSource<int>();
        using var subscriber = SubscribeFromAThread(source, _ => first.TrySetResult(Environment.CurrentManagedThreadId));
        return (subscriber.ThreadId, await first.Task.WaitAsync(TimeSpan.FromSeconds(30)));
    }

    /// <summary>Remembers the threads the test query ran on.</summary>
    public sealed class QueryProbe
    {
        private readonly List<int> _threads = [];

        /// <summary>The query waits here before it answers; open unless a test closes it.</summary>
        public ManualResetEventSlim Gate { get; } = new(true);

        /// <summary>Makes the query throw, to test the error path.</summary>
        public bool Fail { get; init; }

        public int[] Threads { get { lock (_threads) return _threads.ToArray(); } }
        internal void Record() { lock (_threads) _threads.Add(Environment.CurrentManagedThreadId); }
    }

    /// <summary>A query function that notes which thread evaluates it.</summary>
    private sealed class ProbeScalar(QueryProbe probe) : AScalarFunction
    {
        public override void Setup()
        {
            SetName("smm_test_probe");
            AddParameter<long>();
            SetReturnType<long>();
        }

        public override void Execute(ReadOnlyChunk chunk, WritableVector vector)
        {
            probe.Record();
            // Bounded, so a query that runs inside Subscribe fails the test instead of hanging it.
            probe.Gate.Wait(TimeSpan.FromSeconds(20));
            if (probe.Fail) throw new InvalidOperationException("probe failed on purpose");
            var input = chunk.GetVector(0).GetData<long>();
            var output = vector.GetData<long>();
            for (var i = 0; i < (int)chunk.Size; i++) output[i] = input[i];
        }
    }
}
