using System.Reactive.Linq;
using DynamicData;
using DynamicData.Kernel;
using SilverModManager.Abstractions.Collections;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.NexusModsLibrary.Models;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.Query;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;

namespace SilverModManager.Collections;

/// <summary>
/// Reads and writes the rows of collection mods that are waiting for a person.
/// </summary>
public static class PendingInstalls
{
    /// <summary>
    /// Replaces every row of (<paramref name="group"/>, <paramref name="download"/>) with one new row. A replacement
    /// stamp on the old row carries over so the mod still finishes the update it was part of; a dismissal does not,
    /// because a new failure deserves a fresh look.
    /// </summary>
    public static async ValueTask<NexusCollectionPendingInstall.ReadOnly> Record(IConnection connection, EntityId group, CollectionDownload.ReadOnly download, ReviewKind kind, string reason)
    {
        using var tx = connection.BeginTransaction();
        var db = connection.Db;

        var existing = Rows(db, group, CollectionDownloadId.From(download.Id)).ToArray();
        var replaces = existing.Select(static row => row.ReplacesGroup).FirstOrDefault(static value => value.HasValue);
        var wasDisabled = existing.Select(static row => row.ReplacedWasDisabled).FirstOrDefault(static value => value.HasValue);
        foreach (var row in existing) tx.Delete(row.Id, recursive: false);

        var created = new NexusCollectionPendingInstall.New(tx)
        {
            CollectionGroupId = NexusCollectionLoadoutGroupId.From(group),
            DownloadId = CollectionDownloadId.From(download.Id),
            Kind = kind,
            Reason = reason,
        };
        if (replaces.HasValue) tx.Add(created.Id, NexusCollectionPendingInstall.ReplacesGroup, replaces.Value);
        if (wasDisabled.HasValue) tx.Add(created.Id, NexusCollectionPendingInstall.ReplacedWasDisabled, wasDisabled.Value);

        var result = await tx.Commit();
        return NexusCollectionPendingInstall.Load(result.Db, result[created.Id]);
    }

    /// <summary>
    /// Removes every row of (<paramref name="group"/>, <paramref name="download"/>) in <paramref name="tx"/>.
    /// </summary>
    public static void Retract(ITransaction tx, IDb db, EntityId group, CollectionDownloadId download)
    {
        foreach (var row in Rows(db, group, download)) tx.Delete(row.Id, recursive: false);
    }

    /// <summary>
    /// The row of the pair, if there is one.
    /// </summary>
    public static Optional<NexusCollectionPendingInstall.ReadOnly> Find(IDb db, EntityId group, CollectionDownloadId download)
    {
        var rows = Rows(db, group, download).ToArray();
        return rows.Length == 0 ? Optional<NexusCollectionPendingInstall.ReadOnly>.None : rows[0];
    }

    /// <summary>
    /// Every row of the group, dismissed ones included, one per download, leaving out rows that belong to another
    /// revision than the one the group is on.
    /// </summary>
    public static NexusCollectionPendingInstall.ReadOnly[] For(IDb db, EntityId group)
    {
        var loaded = NexusCollectionLoadoutGroup.Load(db, group);
        if (!loaded.IsValid()) return [];
        var revision = loaded.Revision.Id;

        return db.GetBackRefs(NexusCollectionPendingInstall.CollectionGroup, group)
            .AsModels<NexusCollectionPendingInstall.ReadOnly>(db)
            .Where(static row => row.IsValid())
            // DeleteRevision leaves rows behind that point at the download entities it removed.
            .Where(row => CollectionDownload.CollectionRevision.TryGetValue(row.Download, out var rowRevision) && rowRevision == revision)
            .GroupBy(static row => row.DownloadId)
            .Select(static rows => rows.First())
            .ToArray();
    }

    /// <summary>
    /// The optional mods "Install optional" should install: those that are not installed in the group and have no row,
    /// dismissed or not. A pending or dismissed optional mod only comes back through the grid's per-item Install.
    /// </summary>
    public static CollectionDownload.ReadOnly[] OptionalItemsToInstall(IDb db, CollectionRevisionMetadata.ReadOnly revision, EntityId group)
    {
        var loaded = NexusCollectionLoadoutGroup.Load(db, group);
        if (!loaded.IsValid()) return [];

        var collectionGroup = Optional<CollectionGroup.ReadOnly>.Create(loaded.AsCollectionGroup());
        var withRow = For(db, group).Select(static row => row.DownloadId).ToHashSet();
        return CollectionDownloader.GetItems(revision, CollectionDownloader.ItemType.Optional)
            .Where(download => !withRow.Contains(CollectionDownloadId.From(download.Id)))
            .Where(download => !CollectionDownloader.GetStatus(download, collectionGroup, db).IsInstalled(out _))
            .ToArray();
    }

    /// <summary>
    /// The optional mods "Install optional" installs for a loadout that may not hold the collection yet. Without a group
    /// nothing has a row or is installed, so every optional mod is offered.
    /// </summary>
    public static CollectionDownload.ReadOnly[] OptionalItemsToInstall(IDb db, CollectionRevisionMetadata.ReadOnly revision, Optional<NexusCollectionLoadoutGroup.ReadOnly> group) =>
        group.HasValue
            ? OptionalItemsToInstall(db, revision, group.Value.Id)
            : CollectionDownloader.GetItems(revision, CollectionDownloader.ItemType.Optional);

    /// <summary>
    /// How many rows still wait for a person: dismissed rows are left out.
    /// </summary>
    public static int CountUndismissed(IEnumerable<NexusCollectionPendingInstall.ReadOnly> rows) => rows.Count(static row => !IsDismissed(row));

    /// <summary>
    /// <see cref="CountUndismissed"/> of the group's rows, again whenever a row is added, retracted or dismissed. 0 while
    /// there is no group.
    /// </summary>
    public static IObservable<int> ObserveUndismissedCount(IConnection connection, IObservable<Optional<CollectionGroup.ReadOnly>> group) =>
        group
            .Select(g => g.HasValue ? Observe(connection, g.Value.Id).Select(CountUndismissed) : Observable.Return(0))
            .Switch()
            .Prepend(0);

    /// <summary>
    /// Emits the group's rows now and again whenever one is added, retracted or dismissed.
    /// </summary>
    public static IObservable<NexusCollectionPendingInstall.ReadOnly[]> Observe(IConnection connection, EntityId group)
    {
        // A Dismissed datom added to an existing row never shows up in a slice of the CollectionGroup datoms, so the
        // whole attribute is watched too.
        var rowsChanged = connection.ObserveDatoms(NexusCollectionPendingInstall.CollectionGroup, group)
            .QueryWhenChanged(static _ => 0);
        var dismissalsChanged = connection.ObserveDatoms(NexusCollectionPendingInstall.Dismissed)
            .QueryWhenChanged(static _ => 0);
        return rowsChanged.Merge(dismissalsChanged)
            .Select(_ => For(connection.Db, group))
            .Prepend(For(connection.Db, group))
            .DistinctUntilChanged(static rows => string.Join(',', rows.Select(static row => $"{row.Id.Value}:{IsDismissed(row)}")));
    }

    /// <summary>
    /// Marks the row as dismissed. Does nothing when a finishing install already removed it.
    /// </summary>
    public static async ValueTask Dismiss(IConnection connection, EntityId row)
    {
        // Cheap pre-check so a dead row doesn't commit an empty transaction.
        if (!NexusCollectionPendingInstall.Load(connection.Db, row).IsValid()) return;

        using var tx = connection.BeginTransaction();
        // Re-checked against the committing database: an install job may retract the row between the pre-check
        // and the commit, and a Dismissed datom on a dead entity would linger.
        tx.Add(row, static (t, db, id) =>
        {
            if (NexusCollectionPendingInstall.Load(db, id).IsValid()) t.Add(id, NexusCollectionPendingInstall.Dismissed, true);
        });
        await tx.Commit();
    }

    /// <summary>
    /// Whether the user dismissed the row.
    /// </summary>
    public static bool IsDismissed(NexusCollectionPendingInstall.ReadOnly row) =>
        NexusCollectionPendingInstall.Dismissed.TryGetValue(row, out var dismissed) && dismissed;

    private static IEnumerable<NexusCollectionPendingInstall.ReadOnly> Rows(IDb db, EntityId group, CollectionDownloadId download) =>
        db.GetBackRefs(NexusCollectionPendingInstall.CollectionGroup, group)
            .AsModels<NexusCollectionPendingInstall.ReadOnly>(db)
            .Where(row => row.IsValid() && row.DownloadId == download);
}
