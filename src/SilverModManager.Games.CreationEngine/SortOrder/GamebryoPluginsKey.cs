using System.Text.Json;
using SilverModManager.Abstractions.Games.LoadOrder;

namespace SilverModManager.Games.CreationEngine.SortOrder;

/// <summary>Reads the <c>plugins</c> key Vortex's Gamebryo collections carry: a plugin order, so it is not a <see cref="ModOrder"/>.</summary>
public static class GamebryoPluginsKey
{
    /// <summary>
    /// The <c>plugins</c> value Vortex's collections-gamebryo extension writes (see <c>src/util/gameSupport/gamebryo.tsx</c>
    /// in github.com/Nexus-Mods/extension-collections): <c>Array&lt;{ name, enabled? }&gt;</c>, listing only the enabled
    /// plugins, first-loading first. A listed entry without <c>enabled</c> is on. An entry without a name, and a value
    /// that is not an array, are returned as drops.
    /// </summary>
    public static (IReadOnlyList<(string Name, bool IsEnabled)> Plugins, IReadOnlyList<ModOrderDrop> Unmapped) Read(JsonElement plugins)
    {
        var read = new List<(string Name, bool IsEnabled)>();
        var unmapped = new List<ModOrderDrop>();
        if (plugins.ValueKind != JsonValueKind.Array)
        {
            unmapped.Add(new ModOrderDrop("plugins", "the collection's plugin list is not an array, so its plugin order was not applied"));
            return (read, unmapped);
        }

        foreach (var entry in plugins.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.Object
                && entry.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                && name.GetString() is { Length: > 0 } pluginName)
            {
                var enabled = !entry.TryGetProperty("enabled", out var flag) || flag.ValueKind != JsonValueKind.False;
                read.Add((pluginName, enabled));
            }
            else
            {
                unmapped.Add(new ModOrderDrop(entry.ToString(), "a plugin entry without a name, so it was not placed"));
            }
        }
        return (read, unmapped);
    }
}
