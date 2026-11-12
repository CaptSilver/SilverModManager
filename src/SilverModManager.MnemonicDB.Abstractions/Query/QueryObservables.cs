using System.Reactive.Linq;
using System.Reactive.Concurrency;
using DynamicData;
using NexusMods.HyperDuck;

namespace SilverModManager.MnemonicDB.Abstractions.Query;

/// <summary>
/// The one way to turn a query into a live stream of changes.
/// </summary>
public static class QueryObservables
{
    /// <summary>
    /// Like <c>Query&lt;T&gt;.Observe</c>, but the first evaluation runs on the thread pool instead of inside
    /// <c>Subscribe</c>. HyperDuck's own <c>Observe</c> runs the query before it returns, so a view model that
    /// subscribes while it activates would hold the UI thread for as long as DuckDB takes. Later updates already
    /// arrive from HyperDuck's updater thread. Consumers that touch the UI marshal with <c>OnUI()</c> as before; none
    /// may count on the first value being there when <c>Subscribe</c> returns.
    /// <para>
    /// A failing first query still reaches the subscriber as <c>OnError</c>, but now from a thread-pool thread. A
    /// subscriber with no <c>onError</c> and no <c>OnUI()</c> hop before it rethrows there, where it surfaces only as
    /// an unobserved task exception instead of at the <c>Subscribe</c> call.
    /// </para>
    /// </summary>
    public static IObservable<IChangeSet<T, TKey>> ObserveInBackground<T, TKey>(this Query<T> query, Func<T, TKey> keySelector)
        where T : notnull
        where TKey : notnull
    {
        return query.Observe(keySelector).SubscribeOn(TaskPoolScheduler.Default);
    }
}
