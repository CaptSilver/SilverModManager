namespace SilverModManager.Games.CreationEngine;

/// <summary>
/// Thrown when Plugins.txt cannot be written because some plugins' headers could not be read. A plugin left
/// out of Plugins.txt is a disabled plugin, so writing the rest would silently switch mods off.
/// </summary>
public sealed class UnreadablePluginsException(IReadOnlyList<string> pluginNames) : Exception(
    $"Plugins.txt was not written because {(pluginNames.Count == 1 ? "this plugin" : "these plugins")} could not be read: {string.Join(", ", pluginNames)}. " +
    "Verify the game files in the game store, then apply the loadout again.")
{
    public IReadOnlyList<string> PluginNames { get; } = pluginNames;
}
