namespace NexusMods.Games.CreationEngine.Abstractions;

/// <summary>
/// How a game's plugin list file marks a plugin as active.
/// </summary>
public enum PluginListFormat
{
    /// <summary>One plugin file name per line; everything listed is active (Fallout 3, New Vegas, Oblivion, Morrowind).</summary>
    Plain,

    /// <summary>Active plugins are prefixed with <c>*</c> and unmarked ones are inactive (Skyrim SE, Fallout 4 and newer).</summary>
    Star,
}
