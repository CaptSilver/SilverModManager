using SilverModManager.Games.Bethesda.Loot.Masterlists;

namespace SilverModManager.Games.Bethesda.Loot.Conditions;

/// <summary>
/// What a condition can ask about: the files in the game's Data folder, the active plugins, and what is in
/// the plugins' headers. Follows the state loot-condition-interpreter evaluates against.
/// </summary>
public interface IConditionContext
{
    /// <summary>Decides whether <c>.esl</c> counts as a plugin extension and whether ghosted plugins are looked for (never for OpenMW).</summary>
    LootGame Game { get; }

    /// <summary>The game's Data folder on disk. Every path in a condition is relative to it.</summary>
    string DataDirectory { get; }

    /// <summary>The active plugins' file names. They are compared lower-cased, so either case works.</summary>
    IReadOnlySet<string> ActivePlugins { get; }

    /// <summary>Whether the plugin has the master flag. False when it cannot be found or read.</summary>
    bool IsMaster(string pluginFileName);

    /// <summary>The description in the plugin's header, or null when the plugin is unknown or has none.</summary>
    string? PluginDescription(string pluginFileName);
}
