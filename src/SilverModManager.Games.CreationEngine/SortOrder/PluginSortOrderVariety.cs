using System.Collections.Concurrent;
using System.Reactive.Linq;
using DynamicData;
using DynamicData.Kernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.Bethesda.LoadOrder;
using SilverModManager.Games.Bethesda.Loot.Masterlists;
using SilverModManager.Games.CreationEngine.Abstractions;
using SilverModManager.Games.CreationEngine.Loot;
using SilverModManager.Games.CreationEngine.Models;
using NexusMods.Hashing.xxHash3;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.Sdk.Settings;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using OneOf;
using LoadoutSortOrder = SilverModManager.Abstractions.Loadouts.SortOrder;

namespace SilverModManager.Games.CreationEngine.SortOrder;

/// <summary>
/// The plugin load order of a Creation Engine game, one instance per game. Follows the shape of the Cyberpunk
/// REDmod order, with three differences: new plugins go at the end, a row whose plugin left the loadout is kept
/// (see <see cref="Reconcile"/>), and every row has its own on/off switch.
/// </summary>
public sealed class PluginSortOrderVariety : ASortOrderVariety<SortItemKey<string>, PluginReactiveSortItem, PluginLoadoutData, PluginSortData>,
    IPluginSortOrderVariety, IToggleableSortOrderVariety, IReplacedGameEditSource, ILootSortSource
{
    // The live-rows query returns the owning mod's state as flags: a value tuple of more than seven columns is
    // not something the query layer can read.
    private const int LoadoutStateEnabled = 1;
    private const int LoadoutStateGameInstalled = 2;

    private static readonly SortOrderVarietyId StaticVarietyId = SortOrderVarietyId.From(new Guid("6F2B1C0E-7D3A-4B8E-9A61-3C5D2E8F4A17"));

    private readonly IServiceProvider _serviceProvider;
    private readonly ICreationEngineGame _game;
    private readonly PluginHeaderCache _headers;
    private readonly ILogger<PluginSortOrderVariety> _logger;
    private readonly ConcurrentDictionary<Hash, byte> _reportedUnreadable = new();

    public PluginSortOrderVariety(IServiceProvider serviceProvider, ICreationEngineGame game) : base(serviceProvider)
    {
        _serviceProvider = serviceProvider;
        _game = game;
        _headers = new PluginHeaderCache(game);
        _logger = serviceProvider.GetRequiredService<ILogger<PluginSortOrderVariety>>();
    }

    public override SortOrderVarietyId SortOrderVarietyId => StaticVarietyId;

    public override SortOrderUiMetadata SortOrderUiMetadata { get; } = new()
    {
        SortOrderName = "Plugin Load Order",
        OverrideInfoTitle = "Plugin Load Order - Last Loaded Wins",
        OverrideInfoMessage = """
                               When two plugins change the same record, the one that loads later wins. A plugin always loads after the masters it lists, so a move that breaks that is fixed up when the game's plugin list is written.
                               Plugins the game always loads first are locked at the top.
                               """,
        WinnerIndexToolTip = "Last Loaded Plugin Wins: plugins that load later overwrite records from plugins that load before them.",
        IndexColumnHeader = "Load Order",
        DisplayNameColumnHeader = "Plugin",
        EmptyStateMessageTitle = "No plugins detected",
        EmptyStateMessageContents = "Plugins from the game and from your mods appear here once the loadout has any.",
        LearnMoreUrl = "https://github.com/CaptSilver/SilverModManager#readme",
    };

    private string DataFolder => _game.DataPath.Path.ToString();
    private ulong DataLocation => _game.DataPath.LocationId.Value;

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

        var newPluginSortOrder = new PluginSortOrder.New(ts, newSortOrder)
        {
            SortOrder = newSortOrder,
            IsMarker = true,
            SeededFromGameFile = false,
        };

        var commitResult = await ts.Commit();
        return commitResult.Remap(newPluginSortOrder).AsSortOrder().SortOrderId;
    }

    /// <summary>
    /// Reads the headers of every plugin the loadout offers that has not been read yet, so the synchronous
    /// reconciliation that follows can tell masters from plugins.
    /// </summary>
    public override async ValueTask ReconcileSortOrder(SortOrderId sortOrderId, IDb? referenceDb = null, CancellationToken token = default)
    {
        var db = referenceDb ?? Connection.Db;
        var sortOrder = LoadoutSortOrder.Load(db, sortOrderId);
        if (sortOrder.IsValid())
        {
            var plugins = QueryLoadoutPlugins(sortOrder.LoadoutId, db).Select(row => (row.Hash, row.Name));
            await _headers.WarmAsync(plugins);
        }

        await base.ReconcileSortOrder(sortOrderId, referenceDb, token);
    }

    public override IObservable<IChangeSet<PluginReactiveSortItem, SortItemKey<string>>> GetSortOrderItemsChangeSet(SortOrderId sortOrderId)
    {
        var sortOrder = LoadoutSortOrder.Load(Connection.Db, sortOrderId);
        if (!sortOrder.IsValid())
            return Observable.Empty<IChangeSet<PluginReactiveSortItem, SortItemKey<string>>>();

        var loadoutId = sortOrder.LoadoutId;
        var installation = InstallationOf(loadoutId, Connection.Db);
        var early = EarlyLoaderIndexes(installation);

        return Connection.Query<(string PluginName, int SortIndex, bool IsEnabled, Hash Hash, string ModName, EntityId? ModGroupId, int LoadoutState)>($"""
                SELECT PluginName, SortIndex, IsEnabled, Hash, ModName, ModGroupId,
                    CASE WHEN LoadoutIsEnabled THEN 1 ELSE 0 END + CASE WHEN IsGameInstalled THEN 2 ELSE 0 END AS LoadoutState
                FROM plugins.LiveSortOrderItems({Connection}, {sortOrderId.Value}, {loadoutId}, {DataLocation}, {DataFolder})
                """)
            .Observe(row => PluginSortData.KeyFor(row.PluginName))
            .TransformAsync(async row =>
            {
                // Rows can arrive before the reconciliation that would warm their headers has run.
                await _headers.WarmAsync([(row.Hash, row.PluginName)]);
                var data = ToLoadoutData(row.PluginName, row.Hash, (row.LoadoutState & LoadoutStateEnabled) != 0, row.ModName, row.ModGroupId, (row.LoadoutState & LoadoutStateGameInstalled) != 0, installation, early);
                return new PluginReactiveSortItem(row.SortIndex, row.PluginName, row.IsEnabled, data);
            });
    }

    public override IReadOnlyList<PluginReactiveSortItem> GetSortOrderItems(SortOrderId sortOrderId, IDb? db)
    {
        var dbToUse = db ?? Connection.Db;
        var sortOrder = LoadoutSortOrder.Load(dbToUse, sortOrderId);
        if (!sortOrder.IsValid())
            return [];

        // Shows what a reconciliation would store, so a plugin that has just arrived is listed before it is persisted.
        return ReconcileAgainst(sortOrder, dbToUse, dbToUse)
            .Select(tuple => new PluginReactiveSortItem(tuple.SortedEntry.SortIndex, tuple.ItemLoadoutData.Name, tuple.SortedEntry.IsEnabled, tuple.ItemLoadoutData))
            .ToList();
    }

    /// <inheritdoc />
    public override async ValueTask DeleteSortOrder(SortOrderId sortOrderId, CancellationToken token = default)
    {
        await ClearReplacedGameEdit(sortOrderId);
        await base.DeleteSortOrder(sortOrderId, token);
    }

    /// <inheritdoc />
    public Task SetEnabled(SortOrderId sortOrderId, ISortItemKey item, bool enabled, CancellationToken token = default)
    {
        if (item is not SortItemKey<string> key)
            throw new ArgumentException($"A plugin is identified by a string key, not {item.GetType().Name}.", nameof(item));
        return SetEnabled(sortOrderId, key, enabled, null, token);
    }

    /// <inheritdoc />
    public async Task SetEnabled(SortOrderId sortOrderId, SortItemKey<string> item, bool enabled, IDb? db = null, CancellationToken token = default)
    {
        var row = RetrieveSortOrder(sortOrderId, db ?? Connection.Db).FirstOrDefault(r => r.Key.Equals(item))
            ?? throw new InvalidOperationException($"Plugin '{item.Key}' is not in sort order {sortOrderId}.");
        if (row.EarlyLoaderIndex is not null)
            throw new InvalidOperationException($"'{row.Name}' is always loaded by the game and cannot be switched off.");

        if (row.IsGameInstalled)
            throw new InvalidOperationException($"'{row.Name}' was installed by the game and follows its \"Installed by the game\" group; switch the group instead.");

        using var tx = Connection.BeginTransaction();
        tx.Add(row.StoredRowId, PluginSortOrderItem.IsEnabled, enabled);
        await tx.Commit();
    }

    /// <inheritdoc />
    public IReadOnlyList<PluginSortData> StoredOrder(Loadout.ReadOnly loadout)
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
        var row = StoredRows(sortOrderId, Connection.Db).GetValueOrDefault(PluginSortData.KeyFor(name).Key)
            ?? throw new InvalidOperationException($"Plugin '{name}' has no row in sort order {sortOrderId} to switch.");
        tx.Add(row.StoredRowId, PluginSortOrderItem.IsEnabled, enabled);
    }

    /// <inheritdoc />
    public async Task ClearReplacedGameEdit(SortOrderId sortOrderId)
    {
        var notices = Connection.Db.Datoms(PluginListNotice.SortOrder, sortOrderId).Select(d => d.E).ToArray();
        if (notices.Length == 0) return;

        using var tx = Connection.BeginTransaction();
        foreach (var notice in notices)
            tx.Delete(notice, recursive: false);
        await tx.Commit();
    }

    /// <inheritdoc />
    public IObservable<string?> ReplacedEditSummary(SortOrderId sortOrderId) =>
        Connection.Query<(EntityId Id, string Summary)>($"SELECT * FROM plugins.Notices({Connection}, {sortOrderId.Value})")
            .Observe(row => row.Id)
            .QueryWhenChanged(items => items.Items.Select(row => row.Summary).FirstOrDefault())
            .StartWith((string?)null)
            .DistinctUntilChanged();

    /// <inheritdoc />
    Task IReplacedGameEditSource.Dismiss(SortOrderId sortOrderId) => ClearReplacedGameEdit(sortOrderId);

    /// <inheritdoc />
    public Task<LootSortOutcome> SortAsync(SortOrderId sortOrderId, CancellationToken token = default)
    {
        var store = _serviceProvider.GetRequiredService<IMasterlistStore>();
        var dataDirectory = _serviceProvider.GetRequiredService<LootDataDirectory>().Path;
        return new LootSortService(_game, this, Connection, store, dataDirectory, MasterlistRefresh(store), _logger).SortAsync(sortOrderId, token);
    }

    /// <summary>
    /// Downloads a newer masterlist when the store keeps downloaded copies and the user has not turned that off.
    /// </summary>
    private Func<LootGame, CancellationToken, Task>? MasterlistRefresh(IMasterlistStore store)
    {
        if (store is not CachingMasterlistStore caching) return null;
        if (!_serviceProvider.GetRequiredService<ISettingsManager>().Get<LootMasterlistSettings>().LootMasterlistRefresh) return null;

        var client = _serviceProvider.GetRequiredService<HttpClient>();
        return async (game, token) =>
        {
            var result = await caching.RefreshAsync(game, client, token);
            if (result == MasterlistRefreshResult.Failed)
                _logger.LogWarning("Refreshing the LOOT masterlist for {Game} failed; the previous copy stays in use", game);
        };
    }

    /// <summary>
    /// Refuses a list in which the plugins the game always loads first are not the first rows, in their own order.
    /// </summary>
    protected override bool CanApplyOrder(IReadOnlyList<PluginSortData> proposed)
    {
        var locked = proposed.Where(item => item.EarlyLoaderIndex is not null).OrderBy(item => item.EarlyLoaderIndex).ToList();
        return locked.Select((item, index) => ReferenceEquals(proposed[index], item)).All(inPlace => inPlace);
    }

    /// <summary>
    /// The visible rows: those whose plugin the loadout still has. A row without its plugin is kept in the
    /// database but hidden here, so that moving an item by one steps over the rows the user sees.
    /// </summary>
    protected override IReadOnlyList<PluginSortData> RetrieveSortOrder(SortOrderId sortOrderEntityId, IDb dbToUse)
    {
        var sortOrder = LoadoutSortOrder.Load(dbToUse, sortOrderEntityId);
        if (!sortOrder.IsValid())
            return [];

        var early = EarlyLoaderIndexes(InstallationOf(sortOrder.LoadoutId, dbToUse));
        var rows = dbToUse.Connection.Query<(string PluginName, int SortIndex, bool IsEnabled, EntityId Id, bool IsGameInstalled)>($"""
                SELECT PluginName, SortIndex, IsEnabled, Id, IsGameInstalled FROM plugins.LiveSortOrderItems({dbToUse}, {sortOrderEntityId.Value}, {sortOrder.LoadoutId}, {DataLocation}, {DataFolder})
                """)
            .Select(row => new PluginSortData(row.PluginName, row.SortIndex, row.IsEnabled, early.TryGetValue(row.PluginName, out var earlyIndex) ? earlyIndex : null, row.Id) { IsGameInstalled = row.IsGameInstalled })
            .GroupBy(row => row.SortIndex)
            .OrderBy(group => group.Key)
            // A row that was hidden while its plugin was away keeps the index it had and can share it with a row
            // that was indexed since. Only the returning row has not been written for longer, so it goes first.
            .SelectMany(group => group.OrderBy(row => dbToUse.Get(row.StoredRowId).Max(datom => datom.T)))
            .ToList();

        // The base class moves items by list position, so a gap or a repeat left by hidden rows must not show.
        for (var i = 0; i < rows.Count; i++)
            rows[i].SortIndex = i;
        return rows;
    }

    protected override IReadOnlyList<PluginLoadoutData> RetrieveLoadoutData(LoadoutId loadoutId, Optional<CollectionGroupId> collectionGroupId, IDb? db)
    {
        var dbToUse = db ?? Connection.Db;
        var installation = InstallationOf(loadoutId, dbToUse);
        var early = EarlyLoaderIndexes(installation);

        return QueryLoadoutPlugins(loadoutId, dbToUse)
            .Select(row => ToLoadoutData(row.Name, row.Hash, row.IsEnabled, row.ModName, row.ModGroupId, row.IsGameInstalled, installation, early))
            .ToList();
    }

    protected override IReadOnlyList<(PluginSortData SortedEntry, PluginLoadoutData ItemLoadoutData)> ReconcileSortOrderCore(SortOrderId sortOrderId, IDb loadoutRevisionDb)
    {
        var latestDb = loadoutRevisionDb.Connection.Db;
        return ReconcileAgainst(LoadoutSortOrder.Load(latestDb, sortOrderId), latestDb, loadoutRevisionDb);
    }

    /// <summary>
    /// Reconciles without any tombstones to bring back, so every plugin the order does not know is new.
    /// </summary>
    protected override IReadOnlyList<(PluginSortData SortedEntry, PluginLoadoutData ItemLoadoutData)> Reconcile(
        IReadOnlyList<PluginSortData> sourceSortedEntries,
        IReadOnlyList<PluginLoadoutData> loadoutDataItems) =>
        Reconcile(sourceSortedEntries, loadoutDataItems, _ => null);

    /// <summary>
    /// Keeps every visible entry in its place. A plugin the order does not list is looked up among the tombstones
    /// by key and, if the order held it before, comes back at its stored index (clamped to below the locked rows and
    /// to the end) with the switch it had; otherwise it goes at the end, switched on, in the resolver's default
    /// order. Plugins the game always loads first are the exception: stored or new, they go into the locked rows at
    /// the top in game order.
    /// </summary>
    /// <param name="tombstone">Finds the stored row of a plugin by its lower-cased name.</param>
    internal static IReadOnlyList<(PluginSortData SortedEntry, PluginLoadoutData ItemLoadoutData)> Reconcile(
        IReadOnlyList<PluginSortData> sourceSortedEntries,
        IReadOnlyList<PluginLoadoutData> loadoutDataItems,
        Func<string, PluginSortData?> tombstone)
    {
        var loadoutByKey = loadoutDataItems.ToDictionary(item => item.Key);
        var results = new List<(PluginSortData SortedEntry, PluginLoadoutData ItemLoadoutData)>(sourceSortedEntries.Count);
        var seen = new HashSet<SortItemKey<string>>();

        foreach (var entry in sourceSortedEntries.OrderBy(entry => entry.SortIndex))
        {
            // The visible rows all have loadout data; one that does not has just left and is not shown.
            if (!loadoutByKey.TryGetValue(entry.Key, out var data) || !seen.Add(entry.Key)) continue;
            entry.Name = data.Name;
            results.Add((entry, data));
        }

        var fresh = loadoutDataItems.Where(item => !seen.Contains(item.Key)).ToList();

        // The one exception to keeping stored rows in place: every early loader goes into the locked block at the
        // top, in game order, stored or new. The early set is read from the game's files and can grow under an
        // order that already holds the plugin lower down, and CanApplyOrder refuses every move until it is fixed.
        var early = results.Where(r => r.SortedEntry.EarlyLoaderIndex is not null)
            .Concat(fresh.Where(item => item.EarlyLoaderIndex is not null).Select(data => (SortedEntry: New(data), ItemLoadoutData: data)))
            .OrderBy(r => r.SortedEntry.EarlyLoaderIndex)
            .ToList();
        results.RemoveAll(r => r.SortedEntry.EarlyLoaderIndex is not null);
        results.InsertRange(0, early);

        // Tombstones go back in the order they were stored in, so that each insert lands where it was, but never
        // inside the locked block.
        var restored = new List<(PluginLoadoutData Data, PluginSortData Stored)>();
        var added = new List<PluginLoadoutData>();
        foreach (var item in fresh.Where(item => item.EarlyLoaderIndex is null))
        {
            if (tombstone(item.Key.Key) is { } stored) restored.Add((item, stored));
            else added.Add(item);
        }

        foreach (var (data, stored) in restored.OrderBy(r => r.Stored.SortIndex))
            results.Insert(Math.Clamp(stored.SortIndex, early.Count, results.Count), (New(data, stored), data));

        foreach (var data in PluginDefaultOrder.Sort(added))
            results.Add((New(data), data));

        for (var i = 0; i < results.Count; i++)
            results[i].SortedEntry.SortIndex = i;

        return results;

        PluginSortData New(PluginLoadoutData data, PluginSortData? stored = null) =>
            new(data.Name, results.Count, stored?.IsEnabled ?? true, data.EarlyLoaderIndex, stored?.RowId);
    }

    /// <summary>
    /// Upserts the name, index and switch of every row in <paramref name="newOrder"/>, indexed 0..n-1. A stored
    /// row that is not in it, a tombstone, is not touched.
    /// </summary>
    protected override void PersistSortOrderCore(
        SortOrderId sortOrderId,
        IReadOnlyList<PluginSortData> newOrder,
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
            if (stored.TryGetValue(PluginSortData.KeyFor(name).Key, out var row))
            {
                var id = row.StoredRowId;
                if (row.SortIndex != index) tx.Add(id, SortOrderItem.SortIndex, index);
                if (row.Name != name) tx.Add(id, PluginSortOrderItem.PluginName, name);
                if (isEnabled is { } enabled && row.IsEnabled != enabled) tx.Add(id, PluginSortOrderItem.IsEnabled, enabled);
                continue;
            }

            var item = new SortOrderItem.New(tx)
            {
                ParentSortOrderId = sortOrderId,
                SortIndex = index,
            };
            _ = new PluginSortOrderItem.New(tx, item)
            {
                SortOrderItem = item,
                PluginName = name,
                IsEnabled = isEnabled ?? true,
            };
        }
    }

    /// <summary>Every stored row of the sort order, tombstones included, by lower-cased name. The lowest index wins a duplicate.</summary>
    private static Dictionary<string, PluginSortData> StoredRows(SortOrderId sortOrderId, IDb db) =>
        db.Connection.Query<(string PluginName, int SortIndex, bool IsEnabled, EntityId Id)>($"""
                SELECT PluginName, SortIndex, IsEnabled, Id FROM plugins.SortOrderItems({db}, {sortOrderId.Value})
                """)
            .Select(row => new PluginSortData(row.PluginName, row.SortIndex, row.IsEnabled, rowId: row.Id))
            .GroupBy(row => row.Key.Key)
            .ToDictionary(group => group.Key, group => group.OrderBy(row => row.SortIndex).First());

    private IReadOnlyList<(PluginSortData SortedEntry, PluginLoadoutData ItemLoadoutData)> ReconcileAgainst(LoadoutSortOrder.ReadOnly sortOrder, IDb latestDb, IDb loadoutRevisionDb)
    {
        var collectionGroupId = sortOrder.ParentEntity.IsT1 ? sortOrder.ParentEntity.AsT1 : Optional<CollectionGroupId>.None;
        var loadoutData = RetrieveLoadoutData(sortOrder.LoadoutId, collectionGroupId, loadoutRevisionDb);
        var current = RetrieveSortOrder(sortOrder.SortOrderId, latestDb);

        var visible = current.Select(entry => entry.Key.Key).ToHashSet();
        var tombstones = StoredRows(sortOrder.SortOrderId, latestDb);
        return Reconcile(current, loadoutData, key => !visible.Contains(key) ? tombstones.GetValueOrDefault(key) : null);
    }

    private IEnumerable<(string Name, Hash Hash, bool IsEnabled, string ModName, EntityId? ModGroupId, bool IsGameInstalled)> QueryLoadoutPlugins(LoadoutId loadoutId, IDb db) =>
        db.Connection.Query<(string Name, Hash Hash, bool IsEnabled, string ModName, EntityId? ModGroupId, bool IsGameInstalled)>(
            $"SELECT * FROM plugins.LoadoutPlugins({db}, {loadoutId}, {DataLocation}, {DataFolder})");

    private static GameInstallation InstallationOf(LoadoutId loadoutId, IDb db) => Loadout.Load(db, loadoutId).InstallationInstance;

    private Dictionary<string, int> EarlyLoaderIndexes(GameInstallation installation)
    {
        var early = new Dictionary<string, int>(PluginNameComparer.Instance);
        foreach (var (name, index) in _game.EarlyLoadingPlugins(installation).Select((name, index) => (name, index)))
            early[name] = index;
        return early;
    }

    private PluginLoadoutData ToLoadoutData(string name, Hash hash, bool isEnabled, string modName, EntityId? modGroupId, bool isGameInstalled, GameInstallation installation, IReadOnlyDictionary<string, int> early)
    {
        var isMaster = false;
        IReadOnlyList<string> masters = [];
        string? flags = null;
        var isBlueprint = false;

        if (_headers.TryGet(hash, name, out var header))
        {
            if (header is null)
            {
                if (_reportedUnreadable.TryAdd(hash, 0))
                    _logger.LogWarning("The header of plugin {Plugin} ({Hash}) could not be read; it is listed as a plugin with no masters", name, hash);
            }
            else
            {
                // The cache holds the header under a placeholder name, and some flag checks read the extension.
                var named = header with { FileName = name };
                isMaster = _game.IsMaster(named, installation);
                masters = named.Masters;
                flags = FlagsOf(named, isMaster);
                isBlueprint = _game.IsBlueprint(named);
            }
        }

        return new PluginLoadoutData(name, hash, isEnabled, modName, modGroupId, isMaster, masters)
        {
            Flags = flags,
            IsBlueprint = isBlueprint,
            IsGameInstalled = isGameInstalled,
            EarlyLoaderIndex = early.TryGetValue(name, out var index) ? index : null,
        };
    }

    private string? FlagsOf(PluginHeader header, bool isMaster)
    {
        var flags = new List<string>(3);
        if (isMaster) flags.Add("ESM");
        if (_game.IsLight(header)) flags.Add("ESL");
        if (_game.IsMedium(header)) flags.Add("Medium");
        if (_game.IsUpdate(header)) flags.Add("Update");
        if (_game.IsBlueprint(header)) flags.Add("Blueprint");
        return flags.Count == 0 ? null : string.Join(", ", flags);
    }
}
