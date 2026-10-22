using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Games.CreationEngine.Abstractions;
using NexusMods.Games.CreationEngine.SortOrder;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Games.CreationEngine;

public class BethesdaSynchronizer(IServiceProvider provider, IBethesdaGame game) : ALoadoutSynchronizer(provider)
{
    // Evaluated on every path so a game's protected paths can depend on state its constructor sets.
    protected override IGamePathFilter GameSpecificPathFilter { get; } =
        GamePathFilters.Create(game.IsProtectedPath);

    public override Dictionary<GamePath, IIntrinsicFile> IntrinsicFiles(Loadout.ReadOnly loadout) => game.IntrinsicFiles(loadout, provider);

    /// <summary>
    /// Brings the plugin order up to date before the sync tree is built: a plugin installed a moment ago gets its
    /// row (the manager's own reconcile runs in the background), and an order that has never been filled is seeded
    /// from the plugin list the game has on disk. Never done while writing the list, which has no transaction of
    /// its own and must not touch the order it renders.
    /// </summary>
    protected override async ValueTask<Loadout.ReadOnly> PrepareLoadoutForSync(Loadout.ReadOnly loadout)
    {
        if (game is not ICreationEngineGame creationEngineGame) return loadout;

        await game.SortOrderManager.UpdateLoadOrders(loadout.LoadoutId);
        loadout = loadout.Rebase();

        var variety = game.SortOrderManager.GetSortOrderVarieties().OfType<IPluginSortOrderVariety>().Single();
        var sortOrderId = variety.GetSortOrderIdFor(loadout.LoadoutId, loadout.Db)
            .Value;
        if (PluginListSeeder.IsSeeded(loadout.Db, sortOrderId)) return loadout;

        await PluginListSeeder.Seed(creationEngineGame, variety, provider.GetRequiredService<IConnection>(), loadout, sortOrderId);
        return loadout.Rebase();
    }

    public override bool IsIgnoredBackupPath(GamePath path)
    {
        // Don't backup BSA files by default
        return path.Extension == KnownCEExtensions.BSA || path.Extension == KnownCEExtensions.BA2;
    }
}
