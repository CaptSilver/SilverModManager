using System.Text.Json;
using SilverModManager.Abstractions.Games;
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
    public string FixPage(IGame game) => inner.FixPage(game);

    /// <summary>What the plugin drops send the user to: the plugin order's own tab.</summary>
    private string PluginPage => variety.SortOrderUiMetadata.SortOrderName;

    public IReadOnlyList<ModOrderDrop> Apply(Loadout.ReadOnly loadout, ModOrder order, ModOrderSource source, ITransaction tx)
    {
        var drops = new List<ModOrderDrop>();
        if (HasPluginRules(source.Manifest?.PluginRules))
            drops.Add(new ModOrderDrop("LOOT plugin rules and groups", "the app does not apply a collection's LOOT rules", PluginPage));

        if (source.Manifest?.Plugins is not { ValueKind: not JsonValueKind.Null } pluginsKey)
        {
            drops.InsertRange(0, inner.Apply(loadout, order, source, tx));
            return drops;
        }

        var (plugins, unmapped) = GamebryoPluginsKey.Read(pluginsKey);
        if (pluginsKey.ValueKind != JsonValueKind.Array)
        {
            // Nothing usable in the key: the plugin order follows the mod order as it would without one.
            drops.InsertRange(0, inner.Apply(loadout, order, source, tx));
            drops.AddRange(OnPluginPage(unmapped));
            return drops;
        }

        // SeedVarieties=false turns off every variety, which is the plugin variety alone for these games. A plugins key
        // decides the plugin order even when the manifest also has silverLoadOrder: the two order different things.
        drops.InsertRange(0, inner.Apply(loadout, order, source with { SeedVarieties = false }, tx));
        drops.AddRange(OnPluginPage(unmapped));

        var sortOrderId = variety.GetSortOrderIdFor(loadout.LoadoutId, loadout.Db);
        if (!sortOrderId.HasValue)
        {
            drops.Add(new ModOrderDrop("plugins", "the plugin order was not created yet", PluginPage));
            return drops;
        }

        variety.StageOrder(sortOrderId.Value, Merge(loadout, source, plugins), tx);
        return drops;
    }

    /// <summary>
    /// The complete order to store. Vortex lists only the plugins that are on, and its reader switches off every plugin
    /// of the collection's mods that the list leaves out, so those are staged off; plugins of other mods keep their switch.
    /// A run over mods an earlier run installed (<see cref="ModOrderSource.NewGroups"/>) does this for the new mods' plugins
    /// alone and keeps the order and switches the others have.
    /// </summary>
    private List<(string Name, bool? IsEnabled)> Merge(Loadout.ReadOnly loadout, ModOrderSource source, IReadOnlyList<(string Name, bool IsEnabled)> plugins)
    {
        var early = game.EarlyLoadingPlugins(loadout.InstallationInstance).ToHashSet(PluginNameComparer.Instance);
        var stored = variety.StoredOrder(loadout);
        var shipped = variety.PluginsShippedBy(loadout, source.Container, source.NewGroups);
        var listed = source.NewGroups is null
            ? plugins.Select(plugin => (plugin.Name, (bool?)plugin.IsEnabled)).ToList()
            : WithNewPluginsAtTheirSlots(stored, plugins, shipped);
        var merged = PluginOrderMerge.Build(stored, listed, early);

        var unlisted = shipped
            .Except(plugins.Select(plugin => plugin.Name), PluginNameComparer.Instance)
            .Where(name => !early.Contains(name))
            .ToHashSet(PluginNameComparer.Instance);

        var result = merged.Select(entry => unlisted.Remove(entry.Name) ? (entry.Name, (bool?)false) : entry).ToList();
        // A shipped plugin the order has no row for yet is staged hidden and off, so it arrives off.
        result.AddRange(unlisted.Select(name => (name, (bool?)false)));
        return result;
    }

    /// <summary>The stored order with the listed plugins of <paramref name="added"/> moved to their listed slots and given their listed switch.</summary>
    private static List<(string Name, bool? IsEnabled)> WithNewPluginsAtTheirSlots(
        IReadOnlyList<PluginSortData> stored, IReadOnlyList<(string Name, bool IsEnabled)> plugins, IReadOnlySet<string> added)
    {
        var switches = new Dictionary<string, bool>(PluginNameComparer.Instance);
        foreach (var (name, enabled) in plugins) switches.TryAdd(name, enabled);

        var current = stored.OrderBy(static row => row.SortIndex).Select(static row => row.Name).ToList();
        return ListedOrder.InsertNew([..plugins.Select(static plugin => plugin.Name)], current, added.Contains, PluginNameComparer.Instance)
            .Select(name => (name, added.Contains(name) && switches.TryGetValue(name, out var on) ? on : (bool?)null))
            .ToList();
    }

    private IEnumerable<ModOrderDrop> OnPluginPage(IEnumerable<ModOrderDrop> drops) => drops.Select(drop => drop with { Page = PluginPage });

    private static bool HasPluginRules(JsonElement? rules) =>
        rules is { ValueKind: JsonValueKind.Object } value
        && (HasItems(value, "plugins") || HasItems(value, "groups"));

    private static bool HasItems(JsonElement rules, string property) =>
        rules.TryGetProperty(property, out var items) && items.ValueKind == JsonValueKind.Array && items.GetArrayLength() > 0;
}
