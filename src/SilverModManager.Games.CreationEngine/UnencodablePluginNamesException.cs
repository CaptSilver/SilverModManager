namespace NexusMods.Games.CreationEngine;

/// <summary>
/// Thrown when a plugin list cannot be written because a plugin name has a character the game's text encoding
/// can't hold. libloadorder refuses the same way; writing a substitute would name a file that doesn't exist.
/// </summary>
public sealed class UnencodablePluginNamesException(string fileName, string encodingName, IReadOnlyList<string> pluginNames) : Exception(
    $"{fileName} was not written because {(pluginNames.Count == 1 ? "this plugin name" : "these plugin names")} can't be encoded as {encodingName}: {string.Join(", ", pluginNames)}. " +
    "Rename the plugin file to use only characters the game can read.")
{
    public IReadOnlyList<string> PluginNames { get; } = pluginNames;
}
