using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Games.Tools;

/// <summary>
/// Implemented by a game that has plugins a tool can work on one at a time. The Tools page reads it from the
/// loadout's game for the Quick Auto Clean picker, so it never has to know which game family it is looking at.
/// </summary>
public interface IToolPluginSource
{
    /// <summary>The file names of the plugins the loadout's enabled mods put in the game, in name order.</summary>
    IReadOnlyList<string> PluginNames(Loadout.ReadOnly loadout);
}
