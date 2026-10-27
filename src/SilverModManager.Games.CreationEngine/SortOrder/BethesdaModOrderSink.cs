using System.Text.Json;
using SilverModManager.Abstractions.Games.LoadOrder;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.Bethesda.LoadOrder;
using SilverModManager.Games.CreationEngine.Abstractions;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.CreationEngine.SortOrder;

/// <summary>
/// The default priority sink, plus the plugin order a Vortex collection carries in its <c>plugins</c> key. A manifest
/// without the key leaves the plugin order to be seeded from the mod order, as for any other game.
/// </summary>
public sealed class BethesdaModOrderSink(DefaultPriorityOrderSink inner, IPluginSortOrderVariety variety, ICreationEngineGame game) : IModOrderSink
{
    public string FixPage => "Load Order";

    public IReadOnlyList<ModOrderDrop> Apply(Loadout.ReadOnly loadout, ModOrder order, ModOrderSource source, ITransaction tx)
    {
        if (source.Manifest?.Plugins is not { ValueKind: not JsonValueKind.Null } pluginsKey)
            return inner.Apply(loadout, order, source, tx);

        // The inner sink would seed the plugin order from the mod order through the same staging path, and two
        // passes on one sort order in a transaction are not wanted; the manifest's own list replaces that pass.
        var drops = inner.Apply(loadout, order, source with { SeedVarieties = false }, tx).ToList();
        var (plugins, unmapped) = GamebryoPluginsKey.Read(pluginsKey);
        drops.AddRange(unmapped);

        var sortOrderId = variety.GetSortOrderIdFor(loadout.LoadoutId, loadout.Db);
        if (!sortOrderId.HasValue)
        {
            drops.Add(new ModOrderDrop("plugins", "the plugin order was not created yet"));
            return drops;
        }

        // Rows the list does not name keep their order after the listed ones, and the early loaders stay locked in front.
        var early = game.EarlyLoadingPlugins(loadout.InstallationInstance).ToHashSet(PluginNameComparer.Instance);
        variety.StageOrder(sortOrderId.Value, PluginOrderMerge.Build(variety.StoredOrder(loadout), plugins, early), tx);
        return drops;
    }
}
