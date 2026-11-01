using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using DynamicData;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.App.UI.Pages;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;

namespace SilverModManager.App.UI.LeftMenu.Loadout;

/// <inheritdoc />
internal sealed class LibraryNewItems(IConnection connection, IEnumerable<ILibraryDataProvider> providers) : ILibraryNewItems
{
    // Slice changesets arrive on the connection's event thread and MarkSeen on the UI thread. The subject's value is
    // the time of the last sighting; the count filter re-evaluates every item when it changes, so MarkSeen drops the
    // count to zero without anyone resubscribing.
    private readonly ConcurrentDictionary<LoadoutId, BehaviorSubject<DateTimeOffset>> _lastSeen = new();

    private BehaviorSubject<DateTimeOffset> LastSeen(LoadoutId loadout)
        => _lastSeen.GetOrAdd(loadout, static _ => new BehaviorSubject<DateTimeOffset>(DateTimeOffset.UtcNow));

    /// <inheritdoc />
    public void MarkSeen(LoadoutId loadout)
    {
        var subject = LastSeen(loadout);
        lock (subject) subject.OnNext(DateTimeOffset.UtcNow);
    }

    /// <inheritdoc />
    public IObservable<int> ObserveNewCount(LoadoutId loadout, GameId game)
    {
        // Created now, not on subscribe: the menu being built is what starts the clock.
        var lastSeen = LastSeen(loadout);
        var newerThanSeen = lastSeen.Select(seen => (Func<EntityId, bool>)(item => CreatedAt(item) > seen));

        // What counts is what the providers list on the Mods tab; each knows its own rules, so a new source needs no change here.
        return providers
            .Select(provider => provider.ObserveModsTabItems(game))
            .MergeChangeSets()
            .Filter(newerThanSeen)
            .QueryWhenChanged(static query => query.Count)
            .StartWith(0)
            .DistinctUntilChanged();

        DateTimeOffset CreatedAt(EntityId item) => LibraryItem.Load(connection.Db, item).GetCreatedAt();
    }
}
