using System.Reactive.Linq;
using DynamicData;
using DynamicData.Kernel;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.Larian.BaldursGate3.ModList;
using SilverModManager.Games.Larian.BaldursGate3.Models;
using NexusMods.Hashing.xxHash3;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.Sdk.Loadouts;
using OneOf;
using LoadoutSortOrder = SilverModManager.Abstractions.Loadouts.SortOrder;

namespace SilverModManager.Games.Larian.BaldursGate3.SortOrder;

/// <summary>
/// The pak load order of a Baldur's Gate 3 loadout, one instance per game. Follows the shape of the Creation
/// Engine plugin order: new paks go at the end, a row whose pak left the loadout is kept (see
/// <see cref="Reconcile(IReadOnlyList{Bg3PakSortData}, IReadOnlyList{Bg3PakLoadoutData}, Func{string, Bg3PakSortData?})"/>),
/// and every row has its own on/off switch, except the paks the game installed itself, whose switch is their group's.
/// </summary>
public sealed class Bg3PakSortOrderVariety : ASortOrderVariety<SortItemKey<string>, Bg3PakReactiveSortItem, Bg3PakLoadoutData, Bg3PakSortData>,
    IBg3PakSortOrderVariety, IToggleableSortOrderVariety, IReplacedGameEditSource, IHeldGameEditSource
{
    private static readonly SortOrderVarietyId StaticVarietyId = SortOrderVarietyId.From(new Guid("A3D1F6B2-5C84-4E0B-9F27-6B1D0C8E2A55"));

    // Reconciling stores an order too, but it is the app tidying up, not the user changing anything.
    private static readonly AsyncLocal<bool> Reconciling = new();

    public Bg3PakSortOrderVariety(IServiceProvider serviceProvider, IGame game) : base(serviceProvider)
    {
    }

    public override SortOrderVarietyId SortOrderVarietyId => StaticVarietyId;

    public override SortOrderUiMetadata SortOrderUiMetadata { get; } = new()
    {
        SortOrderName = "Mod Load Order",
        OverrideInfoTitle = "Mod Load Order - Last Loaded Wins",
        OverrideInfoMessage = """
                               When two paks change the same data, the one that loads later wins. The game reads the order from its mod list, so a pak that is switched off is left out of it.
                               Paks the game downloaded itself can be moved, but they are switched on and off together.
                               """,
        WinnerIndexToolTip = "Last Loaded Pak Wins: paks that load later overwrite data from paks that load before them.",
        IndexColumnHeader = "Load Order",
        DisplayNameColumnHeader = "Pak",
        EmptyStateMessageTitle = "No paks detected",
        EmptyStateMessageContents = "Paks from your mods and the ones the game downloaded appear here once the loadout has any.",
        LearnMoreUrl = "https://github.com/CaptSilver/SilverModManager#readme",
    };

    private ulong ModsLocation => Bg3Constants.ModsLocationId.Value;

    public override async ValueTask<SortOrderId> GetOrCreateSortOrderFor(
        LoadoutId loadoutId,
        OneOf<LoadoutId, CollectionGroupId> parentEntity,
        CancellationToken token = default)
    {
        var optionalSortOrderId = GetSortOrderIdFor(parentEntity);
        if (optionalSortOrderId.HasValue)
            return optionalSortOrderId.Value;

        token.ThrowIfCancellationRequested();

        using var ts = Connection.BeginTransaction();
        var newSortOrder = new LoadoutSortOrder.New(ts)
        {
            LoadoutId = loadoutId,
            ParentEntity = parentEntity,
            SortOrderTypeId = SortOrderVarietyId.Value,
        };

        var newPakSortOrder = new Bg3PakSortOrder.New(ts, newSortOrder)
        {
            SortOrder = newSortOrder,
            IsMarker = true,
            SeededFromGameFile = false,
        };

        var commitResult = await ts.Commit();
        return commitResult.Remap(newPakSortOrder).AsSortOrder().SortOrderId;
    }

    public override IObservable<IChangeSet<Bg3PakReactiveSortItem, SortItemKey<string>>> GetSortOrderItemsChangeSet(SortOrderId sortOrderId)
    {
        var sortOrder = LoadoutSortOrder.Load(Connection.Db, sortOrderId);
        if (!sortOrder.IsValid())
            return Observable.Empty<IChangeSet<Bg3PakReactiveSortItem, SortItemKey<string>>>();

        var loadoutId = sortOrder.LoadoutId;

        // The query layer cannot read a value tuple of more than seven columns, so the owning mod's two flags
        // travel as one number and are split again below.
        return Connection.Query<(string PakName, int SortIndex, bool IsEnabled, Hash Hash, string ModName, EntityId? ModGroupId, int LoadoutState)>($"""
                SELECT PakName, SortIndex, IsEnabled, Hash, ModName, ModGroupId,
                    CASE WHEN LoadoutIsEnabled THEN 1 ELSE 0 END + CASE WHEN IsGameInstalled THEN 2 ELSE 0 END AS LoadoutState
                FROM bg3.LiveSortOrderItems({Connection}, {sortOrderId.Value}, {loadoutId}, {ModsLocation})
                """)
            .Observe(row => Bg3PakSortData.KeyFor(row.PakName))
            .Transform(row =>
            {
                var data = new Bg3PakLoadoutData(row.PakName, row.Hash, (row.LoadoutState & LoadoutStateEnabled) != 0, row.ModName, row.ModGroupId, (row.LoadoutState & LoadoutStateGameInstalled) != 0);
                return new Bg3PakReactiveSortItem(row.SortIndex, row.PakName, row.IsEnabled, data);
            });
    }

    private const int LoadoutStateEnabled = 1;
    private const int LoadoutStateGameInstalled = 2;

    public override IReadOnlyList<Bg3PakReactiveSortItem> GetSortOrderItems(SortOrderId sortOrderId, IDb? db)
    {
        var dbToUse = db ?? Connection.Db;
        var sortOrder = LoadoutSortOrder.Load(dbToUse, sortOrderId);
        if (!sortOrder.IsValid())
            return [];

        // Shows what a reconciliation would store, so a pak that has just arrived is listed before it is persisted.
        return ReconcileAgainst(sortOrder, dbToUse, dbToUse)
            .Select(tuple => new Bg3PakReactiveSortItem(tuple.SortedEntry.SortIndex, tuple.ItemLoadoutData.Name, tuple.SortedEntry.IsEnabled, tuple.ItemLoadoutData))
            .ToList();
    }

    /// <inheritdoc />
    public override async ValueTask DeleteSortOrder(SortOrderId sortOrderId, CancellationToken token = default)
    {
        // The notice also holds the pak lists the app wrote, which go with the order.
        var notices = Connection.Db.Datoms(Bg3ModListNotice.SortOrder, sortOrderId).Select(d => d.E).ToArray();
        if (notices.Length > 0)
        {
            using var tx = Connection.BeginTransaction();
            foreach (var notice in notices) tx.Delete(notice, recursive: false);
            await tx.Commit();
        }

        await base.DeleteSortOrder(sortOrderId, token);
    }

    /// <inheritdoc />
    public Task SetEnabled(SortOrderId sortOrderId, ISortItemKey item, bool enabled, CancellationToken token = default)
    {
        if (item is not SortItemKey<string> key)
            throw new ArgumentException($"A pak is identified by a string key, not {item.GetType().Name}.", nameof(item));
        return SetEnabled(sortOrderId, key, enabled, null, token);
    }

    /// <inheritdoc />
    public async Task SetEnabled(SortOrderId sortOrderId, SortItemKey<string> item, bool enabled, IDb? db = null, CancellationToken token = default)
    {
        var dbToUse = db ?? Connection.Db;
        var sortOrder = LoadoutSortOrder.Load(dbToUse, sortOrderId);
        var row = RetrieveSortOrder(sortOrderId, dbToUse).FirstOrDefault(r => r.Key.Equals(item))
            ?? throw new InvalidOperationException($"Pak '{item.Key}' is not in sort order {sortOrderId}.");

        if (QueryLoadoutPaks(sortOrder.LoadoutId, dbToUse).Any(pak => pak.IsGameInstalled && Bg3PakSortData.KeyFor(pak.Name).Equals(item)))
            throw new InvalidOperationException($"'{row.Name}' was installed by the game; switch its group instead.");

        using var tx = Connection.BeginTransaction();
        tx.Add(row.StoredRowId, Bg3PakSortOrderItem.IsEnabled, enabled);
        await tx.Commit();
        await ClearModListReset(sortOrderId);
    }

    /// <inheritdoc />
    public IReadOnlyList<Bg3PakSortData> StoredOrder(Loadout.ReadOnly loadout)
    {
        var sortOrderId = GetSortOrderIdFor(loadout.LoadoutId, loadout.Db);
        return sortOrderId.HasValue ? RetrieveSortOrder(sortOrderId.Value, loadout.Db) : [];
    }

    /// <inheritdoc />
    public void StageOrder(SortOrderId sortOrderId, IReadOnlyList<string> names, ITransaction tx) =>
        Stage(sortOrderId, names.Select(name => (name, (bool?)null)).ToList(), tx, Connection.Db);

    /// <inheritdoc />
    public void StageOrder(SortOrderId sortOrderId, IReadOnlyList<(string Name, bool? IsEnabled)> entries, ITransaction tx) =>
        Stage(sortOrderId, entries, tx, Connection.Db);

    /// <inheritdoc />
    public void StageEnabled(SortOrderId sortOrderId, string name, bool enabled, ITransaction tx)
    {
        var row = StoredRows(sortOrderId, Connection.Db).GetValueOrDefault(Bg3PakSortData.KeyFor(name).Key)
            ?? throw new InvalidOperationException($"Pak '{name}' has no row in sort order {sortOrderId} to switch.");
        tx.Add(row.StoredRowId, Bg3PakSortOrderItem.IsEnabled, enabled);
    }

    /// <inheritdoc />
    public async Task ClearReplacedGameEdit(SortOrderId sortOrderId)
    {
        var db = Connection.Db;
        var notices = Bg3ModListNotice.FindBySortOrder(db, sortOrderId).ToArray();
        if (notices.Length == 0) return;

        using var tx = Connection.BeginTransaction();
        var staged = false;
        foreach (var notice in notices)
        {
            // While the list is held the summary says why, and only releasing the hold ends it.
            if (notice.ResetCount.HasValue && notice.ResetCount.Value >= Bg3ModListNotices.HoldAfterResets) continue;

            // The pak lists the app wrote live on the same entity, so only the note is emptied.
            if (Bg3ModListNotices.HoldsMoreThanTheNote(notice))
            {
                if (notice.Summary == "") continue;
                tx.Add(notice.Id, Bg3ModListNotice.Summary, "");
            }
            else
            {
                tx.Delete(notice.Id, recursive: false);
            }
            staged = true;
        }

        // Every Apply runs this, and one that finds no note must not write a transaction.
        if (staged) await tx.Commit();
    }

    /// <inheritdoc />
    public async Task ClearModListReset(SortOrderId sortOrderId)
    {
        var notices = Bg3ModListNotice.FindBySortOrder(Connection.Db, sortOrderId)
            .Where(notice => notice.ResetCount.HasValue && notice.ResetCount.Value > 0)
            .ToArray();
        if (notices.Length == 0) return;

        using var tx = Connection.BeginTransaction();
        foreach (var notice in notices)
        {
            // Below the hold the summary is the ordinary note about a replaced list, which this does not end.
            if (notice.ResetCount.Value >= Bg3ModListNotices.HoldAfterResets) tx.Add(notice.Id, Bg3ModListNotice.Summary, "");
            tx.Add(notice.Id, Bg3ModListNotice.ResetCount, 0);
        }
        await tx.Commit();
    }

    /// <inheritdoc />
    public IObservable<string?> HoldSummary(SortOrderId sortOrderId) =>
        Connection.Query<(EntityId Id, string Summary)>($"SELECT * FROM bg3.HeldNotices({Connection}, {sortOrderId.Value}, {Bg3ModListNotices.HoldAfterResets})")
            .Observe(row => row.Id)
            .QueryWhenChanged(items => items.Items.Select(row => row.Summary).FirstOrDefault())
            .StartWith((string?)null)
            .DistinctUntilChanged();

    /// <inheritdoc />
    Task IHeldGameEditSource.Release(SortOrderId sortOrderId) => ClearModListReset(sortOrderId);

    /// <inheritdoc />
    public override async ValueTask ReconcileSortOrder(SortOrderId sortOrderId, IDb? referenceDb = null, CancellationToken token = default)
    {
        var outer = Reconciling.Value;
        Reconciling.Value = true;
        try
        {
            await base.ReconcileSortOrder(sortOrderId, referenceDb, token);
        }
        finally
        {
            Reconciling.Value = outer;
        }
    }

    /// <summary>An order the user changed puts the app back in charge of the mod list.</summary>
    protected override async ValueTask<bool> TryPersistSortOrder(SortOrderId sortOrderId, IReadOnlyList<Bg3PakSortData> newOrder, IDb startingDb, CancellationToken token = default)
    {
        var persisted = await base.TryPersistSortOrder(sortOrderId, newOrder, startingDb, token);
        if (persisted && !Reconciling.Value) await ClearModListReset(sortOrderId);
        return persisted;
    }

    /// <inheritdoc />
    public IObservable<string?> ReplacedEditSummary(SortOrderId sortOrderId) =>
        Connection.Query<(EntityId Id, string Summary)>($"SELECT * FROM bg3.Notices({Connection}, {sortOrderId.Value}, {Bg3ModListNotices.HoldAfterResets})")
            .Observe(row => row.Id)
            .QueryWhenChanged(items => items.Items.Select(row => row.Summary).FirstOrDefault())
            .StartWith((string?)null)
            .DistinctUntilChanged();

    /// <inheritdoc />
    Task IReplacedGameEditSource.Dismiss(SortOrderId sortOrderId) => ClearReplacedGameEdit(sortOrderId);

    /// <summary>
    /// The visible rows: those whose pak the loadout still has. A row without its pak is kept in the database but
    /// hidden here, so that moving an item by one steps over the rows the user sees.
    /// </summary>
    protected override IReadOnlyList<Bg3PakSortData> RetrieveSortOrder(SortOrderId sortOrderEntityId, IDb dbToUse)
    {
        var sortOrder = LoadoutSortOrder.Load(dbToUse, sortOrderEntityId);
        if (!sortOrder.IsValid())
            return [];

        var rows = dbToUse.Connection.Query<(string PakName, int SortIndex, bool IsEnabled, EntityId Id)>($"""
                SELECT PakName, SortIndex, IsEnabled, Id FROM bg3.LiveSortOrderItems({dbToUse}, {sortOrderEntityId.Value}, {sortOrder.LoadoutId}, {ModsLocation})
                """)
            .Select(row => new Bg3PakSortData(row.PakName, row.SortIndex, row.IsEnabled, row.Id))
            .GroupBy(row => row.SortIndex)
            .OrderBy(group => group.Key)
            // A row that was hidden while its pak was away keeps the index it had and can share it with a row
            // that was indexed since. Only the returning row has not been written for longer, so it goes first.
            .SelectMany(group => group.OrderBy(row => dbToUse.Get(row.StoredRowId).Max(datom => datom.T)))
            .ToList();

        // The base class moves items by list position, so a gap or a repeat left by hidden rows must not show.
        for (var i = 0; i < rows.Count; i++)
            rows[i].SortIndex = i;
        return rows;
    }

    protected override IReadOnlyList<Bg3PakLoadoutData> RetrieveLoadoutData(LoadoutId loadoutId, Optional<CollectionGroupId> collectionGroupId, IDb? db)
    {
        var dbToUse = db ?? Connection.Db;
        return QueryLoadoutPaks(loadoutId, dbToUse)
            .Select(row => new Bg3PakLoadoutData(row.Name, row.Hash, row.IsEnabled, row.ModName, row.ModGroupId, row.IsGameInstalled))
            .ToList();
    }

    protected override IReadOnlyList<(Bg3PakSortData SortedEntry, Bg3PakLoadoutData ItemLoadoutData)> ReconcileSortOrderCore(SortOrderId sortOrderId, IDb loadoutRevisionDb)
    {
        var latestDb = loadoutRevisionDb.Connection.Db;
        return ReconcileAgainst(LoadoutSortOrder.Load(latestDb, sortOrderId), latestDb, loadoutRevisionDb);
    }

    /// <summary>
    /// Reconciles without any tombstones to bring back, so every pak the order does not know is new.
    /// </summary>
    protected override IReadOnlyList<(Bg3PakSortData SortedEntry, Bg3PakLoadoutData ItemLoadoutData)> Reconcile(
        IReadOnlyList<Bg3PakSortData> sourceSortedEntries,
        IReadOnlyList<Bg3PakLoadoutData> loadoutDataItems) =>
        Reconcile(sourceSortedEntries, loadoutDataItems, _ => null);

    /// <summary>
    /// Keeps every visible entry in its place. A pak the order does not list is looked up among the tombstones by
    /// key and, if the order held it before, comes back at its stored index (clamped to the end) with the switch
    /// it had; otherwise it goes at the end, switched on, in name order.
    /// </summary>
    /// <param name="tombstone">Finds the stored row of a pak by its lower-cased name.</param>
    internal static IReadOnlyList<(Bg3PakSortData SortedEntry, Bg3PakLoadoutData ItemLoadoutData)> Reconcile(
        IReadOnlyList<Bg3PakSortData> sourceSortedEntries,
        IReadOnlyList<Bg3PakLoadoutData> loadoutDataItems,
        Func<string, Bg3PakSortData?> tombstone)
    {
        var loadoutByKey = loadoutDataItems.ToDictionary(item => item.Key);
        var results = new List<(Bg3PakSortData SortedEntry, Bg3PakLoadoutData ItemLoadoutData)>(sourceSortedEntries.Count);
        var seen = new HashSet<SortItemKey<string>>();

        foreach (var entry in sourceSortedEntries.OrderBy(entry => entry.SortIndex))
        {
            // The visible rows all have loadout data; one that does not has just left and is not shown.
            if (!loadoutByKey.TryGetValue(entry.Key, out var data) || !seen.Add(entry.Key)) continue;
            entry.Name = data.Name;
            results.Add((entry, data));
        }

        // Tombstones go back in the order they were stored in, so that each insert lands where it was.
        var restored = new List<(Bg3PakLoadoutData Data, Bg3PakSortData Stored)>();
        var added = new List<Bg3PakLoadoutData>();
        foreach (var item in loadoutDataItems.Where(item => !seen.Contains(item.Key)))
        {
            if (tombstone(item.Key.Key) is { } stored) restored.Add((item, stored));
            else added.Add(item);
        }

        foreach (var (data, stored) in restored.OrderBy(r => r.Stored.SortIndex))
            results.Insert(Math.Clamp(stored.SortIndex, 0, results.Count), (New(data, stored), data));

        foreach (var data in added.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.Name, StringComparer.Ordinal))
            results.Add((New(data), data));

        for (var i = 0; i < results.Count; i++)
            results[i].SortedEntry.SortIndex = i;

        return results;

        Bg3PakSortData New(Bg3PakLoadoutData data, Bg3PakSortData? stored = null) =>
            new(data.Name, results.Count, stored?.IsEnabled ?? true, stored?.RowId);
    }

    /// <summary>
    /// Upserts the name, index and switch of every row in <paramref name="newOrder"/>, indexed 0..n-1. A stored
    /// row that is not in it, a tombstone, is not touched.
    /// </summary>
    protected override void PersistSortOrderCore(
        SortOrderId sortOrderId,
        IReadOnlyList<Bg3PakSortData> newOrder,
        ITransaction tx,
        IDb startingDb,
        CancellationToken token = default)
    {
        Stage(sortOrderId, newOrder.Select(item => (item.Name, (bool?)item.IsEnabled)).ToList(), tx, startingDb);
        token.ThrowIfCancellationRequested();
    }

    private void Stage(SortOrderId sortOrderId, IReadOnlyList<(string Name, bool? IsEnabled)> entries, ITransaction tx, IDb db)
    {
        var stored = StoredRows(sortOrderId, db);
        for (var index = 0; index < entries.Count; index++)
        {
            var (name, isEnabled) = entries[index];
            if (stored.TryGetValue(Bg3PakSortData.KeyFor(name).Key, out var row))
            {
                var id = row.StoredRowId;
                if (row.SortIndex != index) tx.Add(id, SortOrderItem.SortIndex, index);
                if (row.Name != name) tx.Add(id, Bg3PakSortOrderItem.PakName, name);
                if (isEnabled is { } enabled && row.IsEnabled != enabled) tx.Add(id, Bg3PakSortOrderItem.IsEnabled, enabled);
                continue;
            }

            var item = new SortOrderItem.New(tx)
            {
                ParentSortOrderId = sortOrderId,
                SortIndex = index,
            };
            _ = new Bg3PakSortOrderItem.New(tx, item)
            {
                SortOrderItem = item,
                PakName = name,
                IsEnabled = isEnabled ?? true,
            };
        }
    }

    /// <summary>Every stored row of the sort order, tombstones included, by lower-cased name. The lowest index wins a duplicate.</summary>
    private static Dictionary<string, Bg3PakSortData> StoredRows(SortOrderId sortOrderId, IDb db) =>
        db.Connection.Query<(string PakName, int SortIndex, bool IsEnabled, EntityId Id)>($"""
                SELECT PakName, SortIndex, IsEnabled, Id FROM bg3.SortOrderItems({db}, {sortOrderId.Value})
                """)
            .Select(row => new Bg3PakSortData(row.PakName, row.SortIndex, row.IsEnabled, row.Id))
            .GroupBy(row => row.Key.Key)
            .ToDictionary(group => group.Key, group => group.OrderBy(row => row.SortIndex).First());

    private IReadOnlyList<(Bg3PakSortData SortedEntry, Bg3PakLoadoutData ItemLoadoutData)> ReconcileAgainst(LoadoutSortOrder.ReadOnly sortOrder, IDb latestDb, IDb loadoutRevisionDb)
    {
        var collectionGroupId = sortOrder.ParentEntity.IsT1 ? sortOrder.ParentEntity.AsT1 : Optional<CollectionGroupId>.None;
        var loadoutData = RetrieveLoadoutData(sortOrder.LoadoutId, collectionGroupId, loadoutRevisionDb);
        var current = RetrieveSortOrder(sortOrder.SortOrderId, latestDb);

        var visible = current.Select(entry => entry.Key.Key).ToHashSet();
        var tombstones = StoredRows(sortOrder.SortOrderId, latestDb);
        return Reconcile(current, loadoutData, key => !visible.Contains(key) ? tombstones.GetValueOrDefault(key) : null);
    }

    private IEnumerable<(string Name, Hash Hash, bool IsEnabled, string ModName, EntityId? ModGroupId, bool IsGameInstalled)> QueryLoadoutPaks(LoadoutId loadoutId, IDb db) =>
        db.Connection.Query<(string Name, Hash Hash, bool IsEnabled, string ModName, EntityId? ModGroupId, bool IsGameInstalled)>(
            $"SELECT * FROM bg3.LoadoutPaks({db}, {loadoutId}, {ModsLocation})");
}
