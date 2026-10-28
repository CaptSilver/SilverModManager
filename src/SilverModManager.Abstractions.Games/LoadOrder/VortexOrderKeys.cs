using System.Text.Json;
using SilverModManager.Abstractions.Collections.Json;

namespace SilverModManager.Abstractions.Games.LoadOrder;

/// <summary>Maps a Vortex per-game key name to the <see cref="CollectionRoot"/> property that holds it, for reading and for writing.</summary>
public static class VortexOrderKeys
{
    public static JsonElement? Get(CollectionRoot root, string key) => key switch
    {
        "loadOrder" => root.LoadOrder,
        "plugins" => root.Plugins,
        _ => throw Unknown(key),
    };

    /// <summary>A copy of <paramref name="root"/> with the key set; null removes it. <see cref="CollectionRoot"/> is init-only, so there is no in-place set.</summary>
    public static CollectionRoot With(CollectionRoot root, string key, JsonElement? value) => key switch
    {
        "loadOrder" => Copy(root, value, root.Plugins),
        "plugins" => Copy(root, root.LoadOrder, value),
        _ => throw Unknown(key),
    };

    /// <summary>A copy of <paramref name="root"/> with <c>mergedData</c> set.</summary>
    public static CollectionRoot WithMergedData(CollectionRoot root, JsonElement value) => new()
    {
        Info = root.Info,
        Mods = root.Mods,
        ModRules = root.ModRules,
        LoadOrder = root.LoadOrder,
        Plugins = root.Plugins,
        PluginRules = root.PluginRules,
        MergedData = value,
        SilverLoadOrder = root.SilverLoadOrder,
    };

    private static CollectionRoot Copy(CollectionRoot root, JsonElement? loadOrder, JsonElement? plugins) => new()
    {
        Info = root.Info,
        Mods = root.Mods,
        ModRules = root.ModRules,
        LoadOrder = loadOrder,
        Plugins = plugins,
        PluginRules = root.PluginRules,
        MergedData = root.MergedData,
        SilverLoadOrder = root.SilverLoadOrder,
    };

    private static ArgumentException Unknown(string key) =>
        new($"'{key}' is not a Vortex order key this app knows (loadOrder, plugins).", nameof(key));
}
