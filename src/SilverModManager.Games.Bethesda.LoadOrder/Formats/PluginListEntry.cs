namespace SilverModManager.Games.Bethesda.LoadOrder.Formats;

/// <summary>
/// One plugin in a plugin list file.
/// </summary>
/// <param name="Name">Plugin file name.</param>
/// <param name="Active">Whether the plugin is switched on.</param>
/// <param name="IsEarlyLoader">Whether the game loads the plugin regardless of the file; only the asterisk format cares.</param>
/// <param name="RenderExplicitly">Write the line even though the plugin is an early loader; only the asterisk format cares.</param>
public sealed record PluginListEntry(string Name, bool Active, bool IsEarlyLoader = false, bool RenderExplicitly = false);
