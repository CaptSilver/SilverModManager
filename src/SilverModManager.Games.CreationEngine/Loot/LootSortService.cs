using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.Bethesda.Loot.Conditions;
using SilverModManager.Games.Bethesda.Loot.Database;
using SilverModManager.Games.Bethesda.Loot.Masterlists;
using SilverModManager.Games.Bethesda.Loot.Sorting;
using SilverModManager.Games.CreationEngine.Abstractions;
using SilverModManager.Games.CreationEngine.SortOrder;
using SilverModManager.MnemonicDB.Abstractions;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using LoadoutSortOrder = SilverModManager.Abstractions.Loadouts.SortOrder;

namespace SilverModManager.Games.CreationEngine.Loot;

/// <summary>
/// Sorts a loadout's plugin order with the LOOT port and stores the result. The masterlist is the store's current
/// one (after a refresh, when one is wired in), the userlist is <c>loot/&lt;repo&gt;/userlist.yaml</c> under
/// <paramref name="dataDirectory"/> when it exists.
/// </summary>
/// <param name="refresh">Brings the store's copy up to date; null when refreshing is off or the store has no network.</param>
internal sealed class LootSortService(
    ICreationEngineGame game,
    IPluginSortOrderVariety variety,
    IConnection connection,
    IMasterlistStore store,
    AbsolutePath dataDirectory,
    Func<LootGame, CancellationToken, Task>? refresh,
    ILogger logger,
    TimeSpan? refreshTimeout = null)
{
    /// <summary>A sort is waiting on the user, so a slow connection must not hold it up for long.</summary>
    private static readonly TimeSpan DefaultRefreshTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Sorts the plugin order and stages it in one transaction; the plugins' switches are not touched. When the
    /// rules form a cycle nothing is stored and the outcome carries the cycle. Any other sorting error propagates.
    /// </summary>
    public async Task<LootSortOutcome> SortAsync(SortOrderId sortOrderId, CancellationToken token)
    {
        var db = connection.Db;
        var sortOrder = LoadoutSortOrder.Load(db, sortOrderId);
        if (!sortOrder.IsValid())
            throw new InvalidOperationException($"Sort order {sortOrderId} does not exist.");

        var installation = Loadout.Load(db, sortOrder.LoadoutId).InstallationInstance;
        var lootGame = game.LootGame(installation);

        await RefreshAsync(lootGame, token);

        // The rows come in stored order, which is the current load order the sorter's tie-breaks start from.
        var items = variety.GetSortOrderItems(sortOrderId, db)
            .Cast<PluginReactiveSortItem>()
            .Select(item => (Item: item, Data: item.LoadoutData as PluginLoadoutData
                ?? throw new InvalidOperationException($"The plugin row '{item.DisplayName}' has no plugin data to sort by")))
            .ToList();
        var plugins = items.Select(i => new LootSortPlugin(i.Data)).ToList();

        var earlyLoading = game.EarlyLoadingPlugins(installation);
        var activeNames = items.Where(i => i.Item.IsActive).Select(i => i.Data.Name).ToList();
        var loadoutData = items.Select(i => i.Data).ToList();

        // Hashing cleaned plugins and reading headers is synchronous disk I/O, and the command that calls this
        // starts on the UI thread.
        return await Task.Run(() => SortPlugins(sortOrderId, installation, lootGame, activeNames, loadoutData, plugins, earlyLoading, token), token);
    }

    private async Task<LootSortOutcome> SortPlugins(
        SortOrderId sortOrderId,
        GameInstallation installation,
        LootGame lootGame,
        List<string> activeNames,
        List<PluginLoadoutData> loadoutData,
        List<LootSortPlugin> plugins,
        IReadOnlyList<string> earlyLoading,
        CancellationToken token)
    {
        var context = new LootConditionContext(
            game,
            installation,
            lootGame,
            installation.Locations.ToAbsolutePath(game.DataPath).ToString(),
            activeNames.Concat(game.InstalledEarlyLoadingPlugins(installation)),
            loadoutData);

        var content = store.Current(lootGame);
        var database = new LootDatabase(new ConditionEvaluator(context));
        database.LoadMasterlistWithPrelude(content.Masterlist, content.Prelude);
        var userlist = dataDirectory / "loot" / lootGame.MasterlistRepository() / "userlist.yaml";
        if (userlist.FileExists)
            database.LoadUserlist(await userlist.ReadAllTextAsync(token));

        token.ThrowIfCancellationRequested();

        try
        {
            var result = PluginSorter.Sort(plugins, database, earlyLoading);

            using var tx = connection.BeginTransaction();
            variety.StageOrder(sortOrderId, result.Order, tx);
            await tx.Commit();

            return new LootSortOutcome(true, null, result.Divergences.OverlapEdgesSkipped, result.Divergences.UnevaluatedConditions, content.Revision);
        }
        catch (CyclicInteractionException cycle)
        {
            // The sorter never got as far as a result, so the divergences come from the same per-plugin
            // preparation it does first.
            var unevaluated = plugins.Select((plugin, index) => PluginSorter.ToPluginSortingData(database, plugin, index).UnevaluatedConditions).Sum();
            return new LootSortOutcome(false, cycle.Message, plugins.Count > 0, unevaluated, content.Revision);
        }
    }

    private async Task RefreshAsync(LootGame lootGame, CancellationToken token)
    {
        if (refresh is null) return;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(refreshTimeout ?? DefaultRefreshTimeout);
        try
        {
            await refresh(lootGame, timeout.Token);
        }
        catch (Exception e) when (!token.IsCancellationRequested)
        {
            // A masterlist that is a few days old sorts nearly as well, so any failure falls back to the current copy.
            logger.LogWarning(e, "Could not refresh the LOOT masterlist for {Game}; sorting with the current copy", lootGame);
        }
    }
}
