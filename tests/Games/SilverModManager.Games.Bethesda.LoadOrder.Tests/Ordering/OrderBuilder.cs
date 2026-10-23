using SilverModManager.Games.Bethesda.LoadOrder.Ordering;

namespace SilverModManager.Games.Bethesda.LoadOrder.Tests.Ordering;

internal static class OrderBuilder
{
    public static PluginOrderEntry Esm(string name, int? index = null, int? early = null, params string[] masters) =>
        new(name, IsMaster: true, IsBlueprint: false, masters, index, early);

    public static PluginOrderEntry Esp(string name, int? index = null, params string[] masters) =>
        new(name, IsMaster: false, IsBlueprint: false, masters, index, null);

    public static PluginOrderEntry Blueprint(string name, int? index = null, params string[] masters) =>
        new(name, IsMaster: true, IsBlueprint: true, masters, index, null);

    /// <summary>Gives every entry a SortIndex equal to its position, as if the user had arranged them in this order.</summary>
    public static PluginOrderEntry[] Indexed(params PluginOrderEntry[] entries) =>
        entries.Select((e, i) => e with { SortIndex = i }).ToArray();

    public static string[] EarlyNames(IEnumerable<PluginOrderEntry> entries) =>
        entries.Where(e => e.EarlyLoaderIndex is not null && !(e.IsMaster && e.IsBlueprint))
            .OrderBy(e => e.EarlyLoaderIndex)
            .Select(e => e.Name)
            .ToArray();

    /// <summary>Hand-built order list from names, for validation tests.</summary>
    public static string[] Names(params string[] names) => names;
}
