using SilverModManager.Games.Bethesda.Loot.Conditions;
using SilverModManager.Games.Bethesda.Loot.Masterlists;

namespace SilverModManager.Games.Bethesda.Loot.Tests.Conditions;

/// <summary>An <see cref="IConditionContext"/> over a temp folder and plain collections, counting the questions asked.</summary>
internal sealed class FakeConditionContext(string dataDirectory, LootGame game = LootGame.SkyrimSE) : IConditionContext
{
    public LootGame Game { get; } = game;

    public string DataDirectory { get; } = dataDirectory;

    public HashSet<string> Active { get; } = [];

    public Dictionary<string, (bool IsMaster, string? Description)> Plugins { get; } = new(StringComparer.OrdinalIgnoreCase);

    public int IsMasterCalls { get; private set; }

    public IReadOnlySet<string> ActivePlugins => Active;

    public bool IsMaster(string pluginFileName)
    {
        IsMasterCalls++;
        return Plugins.TryGetValue(pluginFileName, out var plugin) && plugin.IsMaster;
    }

    public string? PluginDescription(string pluginFileName) =>
        Plugins.TryGetValue(pluginFileName, out var plugin) ? plugin.Description : null;
}
