namespace SilverModManager.Games.Bethesda.LoadOrder;

/// <summary>
/// How many of a FormID's bits Starfield gives a plugin's own records: 24 for a full plugin, 16 for a medium one
/// and 12 for a small (light) one. The rest of the FormID names the plugin.
/// </summary>
public enum PluginScale
{
    Full,
    Medium,
    Small,
}
