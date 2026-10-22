using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Games.Bethesda.Loot.Database;
using NexusMods.Games.Bethesda.Loot.Masterlists;
using NexusMods.Games.Bethesda.Loot.Sorting;
using NexusMods.Games.CreationEngine.Abstractions;
using NexusMods.Games.CreationEngine.SortOrder;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk.Loadouts;
using LoadoutSortOrder = NexusMods.Abstractions.Loadouts.SortOrder;

namespace NexusMods.Games.CreationEngine.Loot;

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

        var content = store.Current(lootGame);
        var database = new LootDatabase();
        database.LoadMasterlistWithPrelude(content.Masterlist, content.Prelude);
        var userlist = dataDirectory / "loot" / lootGame.MasterlistRepository() / "userlist.yaml";
        if (userlist.FileExists)
            database.LoadUserlist(await userlist.ReadAllTextAsync(token));

        token.ThrowIfCancellationRequested();

        // The rows come in stored order, which is the current load order the sorter's tie-breaks start from.
        var plugins = variety.GetSortOrderItems(sortOrderId, db)
            .Cast<PluginReactiveSortItem>()
            .Select(item => new LootSortPlugin(item.LoadoutData as PluginLoadoutData
                ?? throw new InvalidOperationException($"The plugin row '{item.DisplayName}' has no plugin data to sort by")))
            .ToList();

        try
        {
            var result = PluginSorter.Sort(plugins, database, game.EarlyLoadingPlugins(installation));

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
