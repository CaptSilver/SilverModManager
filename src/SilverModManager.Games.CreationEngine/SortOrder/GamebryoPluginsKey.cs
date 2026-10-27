using System.Text.Json;
using SilverModManager.Abstractions.Games.LoadOrder;

namespace SilverModManager.Games.CreationEngine.SortOrder;

/// <summary>Reads the <c>plugins</c> key Vortex's Gamebryo collections carry: a plugin order, so it is not a <see cref="ModOrder"/>.</summary>
public static class GamebryoPluginsKey
{
    /// <summary>
    /// Vortex's collections-gamebryo <c>plugins</c> value. Assumed shape, not yet checked against a real Gamebryo
    /// collection.json: an array of <c>{ "name": "X.esp", "enabled": true }</c>. Anything else in the array is
    /// returned as a drop; so is a value that is not an array.
    /// </summary>
    public static (IReadOnlyList<(string Name, bool? IsEnabled)> Plugins, IReadOnlyList<ModOrderDrop> Unmapped) Read(JsonElement plugins)
    {
        var read = new List<(string Name, bool? IsEnabled)>();
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
                bool? enabled = entry.TryGetProperty("enabled", out var flag) && flag.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? flag.GetBoolean()
                    : null;
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
