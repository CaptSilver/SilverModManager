using System.Collections.Concurrent;
using System.ComponentModel;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using DynamicData;
using DynamicData.Kernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OneOf;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Extensions;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Games.Witcher.Witcher3.Models;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using LoadoutSortOrder = SilverModManager.Abstractions.Loadouts.SortOrder;

namespace SilverModManager.Games.Witcher.Witcher3.SortOrder;

/// <summary>
/// The Witcher 3 Load Order: a view over the loadout's <see cref="LoadoutItemGroupPriority"/> rows with one row per
/// folder of the game's Mods directory. Index 0 is mod0000_MergedFiles, which the game reads first and which wins;
/// the folders after it follow their groups' priority. mods.settings gives a folder its place by that order, so the
/// page and the file never disagree. The page lists the rows in reverse to put the winner at the bottom like every
/// other load order; moves here are in index terms.
/// </summary>
/// <remarks>
/// Only each folder's on/off switch is stored. The index is rebuilt from the priorities on every read, so a stale
/// stored index (the seeding that follows a collection import leaves one) cannot change the page.
/// </remarks>
public sealed class WitcherModSortOrderVariety : ASortOrderVariety<SortItemKey<string>, WitcherModReactiveSortItem, WitcherModLoadoutData, WitcherModSortData>,
    IWitcherModSortOrderVariety, IToggleableSortOrderVariety
{
    private static readonly SortOrderVarietyId StaticVarietyId = SortOrderVarietyId.From(new Guid("cd328482-0604-4aca-9b9a-e0b87e0c0c9a"));

    // Reconciling stores an order too, but it is the app tidying up, not the user dragging a row.
    private static readonly AsyncLocal<bool> Reconciling = new();

    private const string ChangedMessage = "Unable to complete transaction:";

    private static readonly TimeSpan RevisionThrottle = TimeSpan.FromMilliseconds(100);

    private readonly ILoadoutManager _loadouts;
    private readonly ILogger<WitcherModSortOrderVariety> _logger;

    // Rank is asked once per folder and owner for every scan of the loadout, so the folders' ranks are kept per revision.
    private readonly ConcurrentDictionary<LoadoutId, (TxId Basis, IReadOnlyDictionary<string, ConflictPriority> Ranks)> _ranks = new();

    public WitcherModSortOrderVariety(IServiceProvider serviceProvider, IGame game) : base(serviceProvider)
    {
        _loadouts = serviceProvider.GetRequiredService<ILoadoutManager>();
        _logger = serviceProvider.GetRequiredService<ILogger<WitcherModSortOrderVariety>>();
    }

    public override SortOrderVarietyId SortOrderVarietyId => StaticVarietyId;

    // mods.settings gives the folder with the smaller number the higher priority, so index 0 wins.
    public override IndexOverrideBehavior IndexOverrideBehavior => IndexOverrideBehavior.SmallerIndexWins;

    // Shown in reverse so the winner sits at the bottom like on every other load order page.
    public override ListSortDirection SortDirectionDefault => ListSortDirection.Descending;

    public override SortOrderUiMetadata SortOrderUiMetadata { get; } = new()
    {
        SortOrderName = "Load Order",
        OverrideInfoTitle = LoadOrderConvention.Title("Load Order"),
        OverrideInfoMessage = """
                              When two mods in the Mods folder change the same script, the one loaded last, lower in this list, wins. Moving a mod here changes its priority on File Conflicts too.
                              mod0000_MergedFiles holds the scripts the Script Merger combined. It is always loaded and cannot be moved or switched off.
                              """,
        WinnerIndexToolTip = LoadOrderConvention.WinnerToolTip("a mod lower in the list overrides the ones above it."),
        IndexColumnHeader = "Load Order",
        DisplayNameColumnHeader = "Mod folder",
        EmptyStateMessageTitle = "No mods detected",
        EmptyStateMessageContents = "Mods that install into the game's Mods folder appear here once the loadout has any.",
        LearnMoreUrl = "https://github.com/CaptSilver/SilverModManager#readme",
    };

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

        var marker = new WitcherModSortOrder.New(ts, newSortOrder)
        {
            SortOrder = newSortOrder,
            IsMarker = true,
        };

        var commitResult = await ts.Commit();
        return commitResult.Remap(marker).AsSortOrder().SortOrderId;
    }

    public override IObservable<IChangeSet<WitcherModReactiveSortItem, SortItemKey<string>>> GetSortOrderItemsChangeSet(SortOrderId sortOrderId)
    {
        var sortOrder = LoadoutSortOrder.Load(Connection.Db, sortOrderId);
        if (!sortOrder.IsValid())
            return Observable.Empty<IChangeSet<WitcherModReactiveSortItem, SortItemKey<string>>>();

        // The page shows what a read of the three inputs gives: the paths of the loadout's files, the priority rows
        // and the stored switches. A revision that wrote none of their attributes cannot change it.
        var attributes = Connection.Db.AttributeCache;
        HashSet<AttributeId> shown =
        [
            attributes.GetAttributeId(LoadoutItemWithTargetPath.TargetPath.Id),
            attributes.GetAttributeId(LoadoutItem.Name.Id),
            attributes.GetAttributeId(LoadoutItem.Disabled.Id),
            attributes.GetAttributeId(LoadoutItem.Parent.Id),
            attributes.GetAttributeId(LoadoutItemGroupPriority.Priority.Id),
            attributes.GetAttributeId(LoadoutItemGroupPriority.Target.Id),
            attributes.GetAttributeId(LoadoutItemGroupPriority.Loadout.Id),
            attributes.GetAttributeId(WitcherModSortOrderItem.IsEnabled.Id),
            attributes.GetAttributeId(WitcherModSortOrderItem.FolderName.Id),
        ];

        var revisions = Connection.Revisions
            .Publish(shared => shared.Take(1).Merge(
                shared.Skip(1)
                    .Where(db => db.RecentlyAdded.Any(datom => shown.Contains(datom.A)))
                    .Throttle(RevisionThrottle, TaskPoolScheduler.Default)));

        return ObservableChangeSet.Create<WitcherModReactiveSortItem, SortItemKey<string>>(
            cache => revisions.Subscribe(db =>
            {
                // Runs on the task pool for all but the first read, where an escaping exception ends the process.
                // A failed read is logged and the page keeps its rows; the next revision tries again.
                try
                {
                    var items = ItemsOf(sortOrderId, db);
                    cache.Edit(updater =>
                    {
                        var keys = items.Select(static item => item.Key).ToHashSet();
                        updater.Remove(updater.Keys.Where(key => !keys.Contains(key)).ToList());
                        updater.AddOrUpdate(items, ItemComparer.Instance);
                    });
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Reading the Witcher 3 load order of sort order {SortOrder} failed; the page keeps its last rows", sortOrderId);
                }
            }),
            static item => item.Key);
    }

    public override IReadOnlyList<WitcherModReactiveSortItem> GetSortOrderItems(SortOrderId sortOrderId, IDb? db) =>
        ItemsOf(sortOrderId, db ?? Connection.Db);

    /// <inheritdoc />
    public Task SetEnabled(SortOrderId sortOrderId, ISortItemKey item, bool enabled, CancellationToken token = default)
    {
        if (item is not SortItemKey<string> key)
            throw new ArgumentException($"A Mods folder is identified by a string key, not {item.GetType().Name}.", nameof(item));
        return SetEnabled(sortOrderId, key, enabled, token);
    }

    private async Task SetEnabled(SortOrderId sortOrderId, SortItemKey<string> key, bool enabled, CancellationToken token)
    {
        if (key.Equals(WitcherModFolders.MergedKey))
            throw new InvalidOperationException($"{WitcherConstants.W3MergedFolder} is always loaded and cannot be switched off.");

        var db = Connection.Db;
        var row = RetrieveSortOrder(sortOrderId, db).FirstOrDefault(r => r.Key.Equals(key))
            ?? throw new InvalidOperationException($"Folder '{key.Key}' is not in the load order {sortOrderId}.");
        row.IsEnabled = enabled;

        using var tx = Connection.BeginTransaction();
        Stage(sortOrderId, [row], tx, db);

        // The user is answering the card about a replaced mods.settings.
        ModsSettingsNotices.Retract(db, sortOrderId, tx);
        token.ThrowIfCancellationRequested();
        await tx.Commit();
    }

    /// <inheritdoc />
    public IReadOnlyList<(string Folder, bool IsEnabled)> StoredOrder(Loadout.ReadOnly loadout)
    {
        var sortOrderId = GetSortOrderIdFor(loadout.LoadoutId, loadout.Db);
        var rows = sortOrderId.HasValue
            ? RetrieveSortOrder(sortOrderId.Value, loadout.Db)
            : Reconcile([], RetrieveLoadoutData(loadout.LoadoutId, Optional<CollectionGroupId>.None, loadout.Db)).Select(t => t.SortedEntry).ToList();
        return rows.Select(row => (row.Folder, row.IsEnabled)).ToList();
    }

    /// <inheritdoc />
    public ConflictPriority? RankOf(Loadout.ReadOnly loadout, string folder)
    {
        var basis = loadout.Db.BasisTxId;
        if (!_ranks.TryGetValue(loadout.LoadoutId, out var known) || known.Basis != basis)
        {
            var ranks = RetrieveLoadoutData(loadout.LoadoutId, Optional<CollectionGroupId>.None, loadout.Db)
                .ToDictionary(data => data.Key.Key, data => data.Priority);
            known = (basis, ranks);
            _ranks[loadout.LoadoutId] = known;
        }

        return known.Ranks.TryGetValue(WitcherModFolders.KeyFor(folder).Key, out var rank) ? rank : null;
    }

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

    /// <summary>
    /// A drag changes the order the page shows, which means the priorities of the groups behind it. The drag goes to
    /// the loadout manager in one transaction with the switches; if a stored rule forbids it, nothing is written and
    /// the rule is named. Anything that leaves the order as it is, and every reconcile, only stores the switches.
    /// </summary>
    protected override async ValueTask<bool> TryPersistSortOrder(SortOrderId sortOrderId, IReadOnlyList<WitcherModSortData> newOrder, IDb startingDb, CancellationToken token = default)
    {
        if (Reconciling.Value) return await base.TryPersistSortOrder(sortOrderId, newOrder, startingDb, token);

        // The merged folder is pinned, so it is left out by key. Where a drag put it in the proposal means nothing.
        var proposed = newOrder.Where(row => !row.Key.Equals(WitcherModFolders.MergedKey)).Select(row => row.Key).ToList();
        var shownKeys = RetrieveSortOrder(sortOrderId, startingDb).Where(row => !row.Key.Equals(WitcherModFolders.MergedKey)).Select(row => row.Key).ToList();
        if (proposed.SequenceEqual(shownKeys)) return await base.TryPersistSortOrder(sortOrderId, newOrder, startingDb, token);

        var loadoutId = LoadoutSortOrder.Load(startingDb, sortOrderId).LoadoutId;
        var groupsOf = RetrieveLoadoutData(loadoutId, Optional<CollectionGroupId>.None, startingDb).ToDictionary(data => data.Key, data => data.Groups);

        // A mod that puts files in two folders is one group; it takes the place of the first folder that lists it.
        var winnerToLoser = proposed.SelectMany(key => groupsOf[key]).Distinct().ToArray();

        // Priorities belong to groups, so a move that leaves the groups in the order their priority rows already have
        // changes nothing. The shown folder order is not that order when a group's files fill several folders.
        var requested = winnerToLoser.ToHashSet();
        var current = LoadoutItemGroupPriority.FindByLoadout(startingDb, loadoutId)
            .Where(row => row.Target.IsValid() && requested.Contains(row.TargetId))
            .OrderByDescending(row => row.Priority)
            .ThenBy(row => row.TargetId.Value)
            .Select(row => row.TargetId);
        if (winnerToLoser.SequenceEqual(current))
            throw new InvalidOperationException($"{string.Join(" and ", SharedFoldersInDrag(shownKeys, proposed, groupsOf, newOrder))} come from the same mod and move together.");

        using var tx = Connection.BeginTransaction();
        var result = _loadouts.SetGroupOrder(loadoutId, winnerToLoser, tx);
        if (result.Overridden.Count > 0)
            throw new InvalidOperationException($"That move would break the rule '{GroupRuleText.WinsOver(result.Overridden[0].Rule)}'.");

        PersistSortOrderCore(sortOrderId, newOrder, tx, startingDb, token);

        // A drag is the user answering the card about a replaced mods.settings. Not done in PersistSortOrderCore: that
        // also runs for reconciles and for the seeding after a collection import, neither of which answers anything.
        ModsSettingsNotices.Retract(startingDb, sortOrderId, tx);

        // The same guard as the base class: a switch or reconcile that landed since startingDb must not be overwritten.
        var sortOrderTxId = GetMaxTxIdForSortOrder(sortOrderId, startingDb);
        if (sortOrderTxId == default(TxId)) return false;
        tx.Add(sortOrderId, sortOrderTxId, (_, txDb, orderId, oldTxId) =>
        {
            var currentTxId = GetMaxTxIdForSortOrder(orderId, txDb);
            if (currentTxId > oldTxId || currentTxId == default(TxId))
                throw new InvalidOperationException($"{ChangedMessage} Sort Order {orderId}, Current TxId: {currentTxId}, Old TxId: {oldTxId}");
        });

        try
        {
            await tx.Commit();
        }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith(ChangedMessage, StringComparison.Ordinal))
        {
            _logger.LogWarning(ex, "The Witcher 3 load order {SortOrderId} changed during a move", sortOrderId);
            throw new InvalidOperationException("The load order changed while you were moving it; try again.");
        }

        return true;
    }

    /// <summary>
    /// The folders of this drag that share a mod with another folder: those whose place changed, and the folders they
    /// share a group with, in page order.
    /// </summary>
    private static List<string> SharedFoldersInDrag(
        IReadOnlyList<SortItemKey<string>> shown,
        IReadOnlyList<SortItemKey<string>> proposed,
        IReadOnlyDictionary<SortItemKey<string>, IReadOnlyList<LoadoutItemGroupId>> groupsOf,
        IReadOnlyList<WitcherModSortData> rows)
    {
        var moved = shown.Where(key => shown.IndexOf(key) != proposed.IndexOf(key)).ToHashSet();
        bool Together(SortItemKey<string> x, SortItemKey<string> y) =>
            !x.Equals(y) && groupsOf[x].Intersect(groupsOf[y]).Any() && (moved.Contains(x) || moved.Contains(y));

        var names = rows.ToDictionary(row => row.Key, row => row.Folder);
        return shown.Where(key => shown.Any(other => Together(key, other))).Select(key => names[key]).ToList();
    }

    /// <summary>The rows the page shows now; see <see cref="ReadView"/>.</summary>
    protected override IReadOnlyList<WitcherModSortData> RetrieveSortOrder(SortOrderId sortOrderId, IDb db) =>
        ReadView(sortOrderId, db, db).Select(tuple => tuple.SortedEntry).ToList();

    protected override IReadOnlyList<WitcherModLoadoutData> RetrieveLoadoutData(LoadoutId loadoutId, Optional<CollectionGroupId> collectionGroupId, IDb? db)
    {
        var dbToUse = db ?? Connection.Db;
        var folders = new Dictionary<SortItemKey<string>, FolderGroups>();

        // Model accessors only: this runs inside the synchronizer's winning-files query through RankOf, where a
        // database query would re-enter the one that is running.
        var priorities = new Dictionary<LoadoutItemGroupId, ConflictPriority?>();
        foreach (var item in LoadoutItem.FindByLoadout(dbToUse, loadoutId).OfTypeLoadoutItemWithTargetPath())
        {
            if (!item.TryGetAsLoadoutFile(out _)) continue;
            if (WitcherModFolders.FolderOf(item.TargetPath) is not { } folder) continue;
            if (WitcherModFolders.KeyFor(folder).Equals(WitcherModFolders.MergedKey)) continue;

            var chain = item.AsLoadoutItem().GetThisAndParents().ToArray();
            if (chain.Any(p => p.IsDisabled)) continue;

            // The priority sits on the mod's group; a collection's rows are remapped onto the mods inside it, so the
            // first group above the file with a row is the mod. A file under no ranked group is ranked last.
            LoadoutItem.ReadOnly? owner = null;
            ConflictPriority? priority = null;
            foreach (var ancestor in chain.Skip(1))
            {
                var id = LoadoutItemGroupId.From(ancestor.LoadoutItemId.Value);
                if (!priorities.TryGetValue(id, out var row))
                    priorities[id] = row = LoadoutItemGroupPriority.FindByTarget(dbToUse, id).Select(r => (ConflictPriority?)r.Priority).Max();
                if (row is null) continue;

                owner = ancestor;
                priority = row;
                break;
            }
            owner ??= chain.Length > 1 ? chain[1] : null;
            if (owner is not { } mod) continue;

            var key = WitcherModFolders.KeyFor(folder);
            if (!folders.TryGetValue(key, out var groups)) folders[key] = groups = new FolderGroups();
            groups.Add(LoadoutItemGroupId.From(mod.LoadoutItemId.Value), mod.Name, priority ?? ConflictPriority.MinValue, folder);
        }

        return folders.Values.Select(groups => groups.ToLoadoutData()).ToList();
    }

    /// <summary>
    /// The live view: the loadout's folders in the order <see cref="Reconcile"/> gives them, each with the stored switch
    /// and indexed 0..n-1. Reads the stored rows from <paramref name="storedDb"/> and the loadout from
    /// <paramref name="loadoutDb"/>, which reconciling takes from different revisions.
    /// </summary>
    private IReadOnlyList<(WitcherModSortData SortedEntry, WitcherModLoadoutData ItemLoadoutData)> ReadView(SortOrderId sortOrderId, IDb storedDb, IDb loadoutDb)
    {
        var sortOrder = LoadoutSortOrder.Load(storedDb, sortOrderId);
        if (!sortOrder.IsValid()) return [];

        var data = RetrieveLoadoutData(sortOrder.LoadoutId, Optional<CollectionGroupId>.None, loadoutDb);
        return Reconcile(StoredRows(sortOrderId, storedDb).Values.ToList(), data);
    }

    protected override IReadOnlyList<(WitcherModSortData SortedEntry, WitcherModLoadoutData ItemLoadoutData)> ReconcileSortOrderCore(SortOrderId sortOrderId, IDb loadoutRevisionDb) =>
        ReadView(sortOrderId, loadoutRevisionDb.Connection.Db, loadoutRevisionDb);

    /// <summary>
    /// Merged folder first and locked, then the loadout's folders by priority, highest first, ties by folder name. The
    /// stored rows are read for the switch and the database row only: their index is not an input.
    /// </summary>
    protected override IReadOnlyList<(WitcherModSortData SortedEntry, WitcherModLoadoutData ItemLoadoutData)> Reconcile(
        IReadOnlyList<WitcherModSortData> sourceSortedEntries,
        IReadOnlyList<WitcherModLoadoutData> loadoutDataItems)
    {
        var stored = new Dictionary<SortItemKey<string>, WitcherModSortData>();
        foreach (var entry in sourceSortedEntries.OrderBy(entry => entry.SortIndex))
            stored.TryAdd(entry.Key, entry);

        var merged = new WitcherModLoadoutData(WitcherConstants.W3MergedFolder, ConflictPriority.MaxValue, [], true, "Merged files");
        var ordered = loadoutDataItems
            .OrderByDescending(item => item.Priority)
            .ThenBy(item => item.Folder, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Folder, StringComparer.Ordinal)
            .Prepend(merged)
            .ToList();

        var results = new List<(WitcherModSortData SortedEntry, WitcherModLoadoutData ItemLoadoutData)>(ordered.Count);
        for (var i = 0; i < ordered.Count; i++)
        {
            var data = ordered[i];
            stored.TryGetValue(data.Key, out var row);
            var isMerged = i == 0;
            results.Add((new WitcherModSortData(data.Folder, i, isMerged || (row?.IsEnabled ?? true), row?.RowId), data));
        }

        return results;
    }

    /// <summary>
    /// Upserts the folder name, index and switch of every row in <paramref name="newOrder"/>. The merged folder has
    /// nothing to store. The index is written for the base class's bookkeeping and never read back.
    /// </summary>
    protected override void PersistSortOrderCore(
        SortOrderId sortOrderId,
        IReadOnlyList<WitcherModSortData> newOrder,
        ITransaction tx,
        IDb startingDb,
        CancellationToken token = default)
    {
        Stage(sortOrderId, newOrder, tx, startingDb);
        token.ThrowIfCancellationRequested();
    }

    private void Stage(SortOrderId sortOrderId, IReadOnlyList<WitcherModSortData> rows, ITransaction tx, IDb db)
    {
        var stored = StoredRows(sortOrderId, db);
        foreach (var row in rows)
        {
            var index = row.SortIndex;
            if (row.Key.Equals(WitcherModFolders.MergedKey)) continue;

            if (stored.TryGetValue(row.Key, out var existing) && existing.RowId is { } id)
            {
                if (existing.SortIndex != index) tx.Add(id, SortOrderItem.SortIndex, index);
                if (existing.Folder != row.Folder) tx.Add(id, WitcherModSortOrderItem.FolderName, row.Folder);
                if (existing.IsEnabled != row.IsEnabled) tx.Add(id, WitcherModSortOrderItem.IsEnabled, row.IsEnabled);
                continue;
            }

            var item = new SortOrderItem.New(tx)
            {
                ParentSortOrderId = sortOrderId,
                SortIndex = index,
            };
            _ = new WitcherModSortOrderItem.New(tx, item)
            {
                SortOrderItem = item,
                FolderName = row.Folder,
                IsEnabled = row.IsEnabled,
            };
        }
    }

    /// <summary>The stored rows by folder key, the lowest stored index winning a duplicate.</summary>
    private static Dictionary<SortItemKey<string>, WitcherModSortData> StoredRows(SortOrderId sortOrderId, IDb db)
    {
        var rows = new Dictionary<SortItemKey<string>, WitcherModSortData>();
        foreach (var item in SortOrderItem.FindByParentSortOrder(db, sortOrderId).OrderBy(item => item.SortIndex))
        {
            var folder = WitcherModSortOrderItem.Load(db, item.Id);
            if (!folder.IsValid() || !folder.FolderName.HasValue) continue;

            var name = folder.FolderName.Value;
            rows.TryAdd(WitcherModFolders.KeyFor(name), new WitcherModSortData(name, item.SortIndex, !folder.IsEnabled.HasValue || folder.IsEnabled.Value, item.Id));
        }

        return rows;
    }

    private List<WitcherModReactiveSortItem> ItemsOf(SortOrderId sortOrderId, IDb db) =>
        ReadView(sortOrderId, db, db)
            .Select(tuple => new WitcherModReactiveSortItem(tuple.SortedEntry.SortIndex, tuple.SortedEntry.IsEnabled, tuple.ItemLoadoutData))
            .ToList();

    /// <summary>The groups that put a file into one folder, collected while the loadout is read.</summary>
    private sealed class FolderGroups
    {
        private readonly Dictionary<LoadoutItemGroupId, (string Name, ConflictPriority Priority, string Spelling)> _groups = new();

        public void Add(LoadoutItemGroupId group, string name, ConflictPriority priority, string spelling)
        {
            // A group can spell the folder two ways; the lowest spelling keeps the choice stable between reads.
            if (_groups.TryGetValue(group, out var known) && string.CompareOrdinal(known.Spelling, spelling) <= 0) return;
            _groups[group] = (name, priority, spelling);
        }

        public WitcherModLoadoutData ToLoadoutData()
        {
            var ranked = _groups
                .OrderByDescending(g => g.Value.Priority)
                .ThenBy(g => g.Key.Value)
                .ToList();
            var top = ranked[0];
            return new WitcherModLoadoutData(top.Value.Spelling, top.Value.Priority, ranked.Select(g => g.Key).ToList(), true, top.Value.Name);
        }
    }

    private sealed class ItemComparer : IEqualityComparer<WitcherModReactiveSortItem>
    {
        public static readonly ItemComparer Instance = new();

        public bool Equals(WitcherModReactiveSortItem? x, WitcherModReactiveSortItem? y) =>
            ReferenceEquals(x, y) || (x is not null && y is not null && x.Key.Equals(y.Key) && x.SortIndex == y.SortIndex
                                      && x.DisplayName == y.DisplayName && x.ModName == y.ModName && x.IsActive == y.IsActive
                                      && x.IsEnabled == y.IsEnabled && x.ModGroupId.Equals(y.ModGroupId));

        public int GetHashCode(WitcherModReactiveSortItem item) => item.Key.GetHashCode();
    }
}
