using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Games.LoadOrder;

/// <summary>
/// Writes the order into the loadout's priority rows and switches groups on or off as the order says.
/// </summary>
public sealed class DefaultPriorityOrderSink(ILoadoutManager loadouts, ILogger<DefaultPriorityOrderSink> logger) : IModOrderSink
{
    public string FixPage => "Load Order";

    public IReadOnlyList<ModOrderDrop> Apply(Loadout.ReadOnly loadout, ModOrder order, ModOrderSource source, ITransaction tx)
    {
        var result = loadouts.SetGroupOrder(loadout.LoadoutId, [..order.WinnerToLoser.Select(static entry => entry.Group)], tx);

        foreach (var entry in order.WinnerToLoser)
        {
            if (entry.Enabled is not { } enabled) continue;

            var item = LoadoutItem.Load(loadout.Db, entry.Group);
            if (!enabled && !item.IsDisabled) tx.Add(item.Id, LoadoutItem.Disabled, Null.Instance);
            else if (enabled && item.IsDisabled) tx.Retract(item.Id, LoadoutItem.Disabled, Null.Instance);
        }

        logger.LogTrace("Ordered {Count} groups of '{Source}'; {Overridden} pairs were turned round by stored rules", order.WinnerToLoser.Count, source.Name, result.Overridden.Count);

        return
        [
            ..result.Overridden.Select(overridden => new ModOrderDrop(
                $"{Name(loadout, overridden.RequestedWinner)} above {Name(loadout, overridden.RequestedLoser)}",
                overridden.Displaced
                    ? $"the order moved to make room for the stored rule '{GroupRuleText.WinsOver(overridden.Rule)}'"
                    : $"the stored rule '{GroupRuleText.WinsOver(overridden.Rule)}' puts them the other way round")),
        ];
    }

    private static string Name(Loadout.ReadOnly loadout, LoadoutItemGroupId group) => LoadoutItem.Load(loadout.Db, group).Name;
}
