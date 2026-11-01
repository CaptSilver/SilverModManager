using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using DynamicData;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Abstractions.NexusModsLibrary;
using SilverModManager.Abstractions.NexusModsLibrary.Models;
using SilverModManager.MnemonicDB.Abstractions.DatomIterators;
using SilverModManager.App.UI.Pages;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using SilverModManager.Wabbajack.Models;

namespace SilverModManager.App.UI.LeftMenu.Loadout;

/// <inheritdoc />
internal sealed class LibraryNewItems(IConnection connection) : ILibraryNewItems
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
        var newerThanSeen = lastSeen.Select(seen => (Func<Datom, bool>)(datom => CreatedAt(datom) > seen));

        return LibraryDestinations.ObserveShownFor(connection, game)
            // Items already seen go first, so the back-reference slices below are only watched for items that could count.
            .Filter(newerThanSeen)
            .FilterImmutable(datom => IsListedOnModsTab(connection.Db, datom.E))
            .FilterOnObservable((_, id) => LibraryDataProviderHelper.IsShownInMods(connection, id, game))
            .QueryWhenChanged(static query => query.Count)
            .StartWith(0)
            .DistinctUntilChanged();

        DateTimeOffset CreatedAt(Datom datom) => LibraryItem.Load(connection.Db, datom.E).GetCreatedAt();
    }

    private static bool IsPlainLocalFile(IDb db, EntityId item)
    {
        var local = LocalFile.Load(db, item);
        return local.IsValid() && !LocalFileDataProvider.IsModlist(local);
    }

    // The models some provider lists on the Mods tab. An archive member has no destination, so it is never in the slice;
    // a modlist is on the Collections tab.
    private static bool IsListedOnModsTab(IDb db, EntityId item)
        => IsPlainLocalFile(db, item)
           || ManuallyCreatedArchive.Load(db, item).IsValid()
           || NexusModsLibraryItem.Load(db, item).IsValid()
           || ThunderstoreLibraryFile.Load(db, item).IsValid();
}
