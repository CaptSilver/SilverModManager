using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Games.LoadOrder;

/// <summary>Runs an order through the game's sink and stores what could not be placed, for the Health Check.</summary>
public sealed class ModOrderImporter(IServiceProvider provider, IConnection connection, ILogger<ModOrderImporter> logger)
{
    /// <summary>
    /// Applies <paramref name="order"/> to the loadout through the game's sink (<see cref="IModOrderGame.OrderSink"/>, else
    /// <see cref="DefaultPriorityOrderSink"/>) inside <paramref name="tx"/>, and replaces the loadout's <see cref="ModOrderDroppedEntry"/>
    /// rows whose container is <paramref name="source"/>'s with <see cref="ModOrder.Dropped"/> plus what the sink dropped, each
    /// row carrying <see cref="ModOrderSource.Name"/> as its source and the drop's page, else the sink's. Each drop is also a Trace log.
    /// </summary>
    public void Import(Loadout.ReadOnly loadout, ModOrder order, ModOrderSource source, ITransaction tx)
    {
        var game = loadout.InstallationInstance.GetGame();
        var sink = SinkFor(game, provider);
        IReadOnlyList<ModOrderDrop> sinkDrops = sink.Apply(loadout, order, source, tx);

        // Read the committed rows rather than loadout.Db, which may predate an earlier import of the same container.
        foreach (var existing in ModOrderDroppedEntry.FindByLoadout(connection.Db, loadout.LoadoutId))
        {
            if (existing.ContainerId == source.Container)
                tx.Delete(existing.Id, recursive: false);
        }

        foreach (var drop in order.Dropped.Concat(sinkDrops))
        {
            logger.LogTrace("'{Source}' order dropped '{Item}': {Reason}", source.Name, drop.Item, drop.Reason);
            _ = new ModOrderDroppedEntry.New(tx)
            {
                LoadoutId = loadout.LoadoutId,
                ContainerId = source.Container,
                Source = source.Name,
                Item = drop.Item,
                Reason = drop.Reason,
                Page = drop.Page ?? sink.FixPage(game),
            };
        }
    }

    public static IModOrderSink SinkFor(IGame game, IServiceProvider provider) =>
        game is IModOrderGame { OrderSink: { } own } ? own : provider.GetRequiredService<DefaultPriorityOrderSink>();
}
