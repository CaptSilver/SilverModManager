using SilverModManager.Games.Bethesda.LoadOrder;
using SilverModManager.Games.Bethesda.Loot.Conditions;
using SilverModManager.Games.Bethesda.Loot.Masterlists;
using SilverModManager.Games.CreationEngine.Abstractions;
using SilverModManager.Games.CreationEngine.SortOrder;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.CreationEngine.Loot;

/// <summary>
/// What LOOT's conditions see during a sort: the game's Data folder on disk, the plugins the loadout switches on
/// plus the ones the game always loads, and the master flag and description of each plugin. A plugin the loadout
/// lists is answered from what the loadout already knows; any other name is looked up in the Data folder and its
/// header read once.
/// </summary>
/// <remarks>
/// The Data folder is the game's folder as it is now, which is what LOOT would see after the loadout is applied.
/// Conditions are evaluated synchronously, so everything the loadout knows is gathered up front.
/// </remarks>
internal sealed class LootConditionContext : IConditionContext
{
    private readonly ICreationEngineGame _game;
    private readonly GameInstallation _installation;
    private readonly Dictionary<string, PluginFacts> _facts;

    public LootConditionContext(
        ICreationEngineGame game,
        GameInstallation installation,
        LootGame lootGame,
        string dataDirectory,
        IEnumerable<string> activePlugins,
        IEnumerable<PluginLoadoutData> loadoutPlugins)
    {
        _game = game;
        _installation = installation;
        Game = lootGame;
        DataDirectory = dataDirectory;
        ActivePlugins = activePlugins.ToHashSet(StringComparer.OrdinalIgnoreCase);

        _facts = new Dictionary<string, PluginFacts>(StringComparer.OrdinalIgnoreCase);
        foreach (var plugin in loadoutPlugins)
            _facts[plugin.Name] = new PluginFacts(plugin.IsMaster, plugin.Description);
    }

    public LootGame Game { get; }

    public string DataDirectory { get; }

    public IReadOnlySet<string> ActivePlugins { get; }

    public bool IsMaster(string pluginFileName) => FactsFor(pluginFileName).IsMaster;

    public string? PluginDescription(string pluginFileName) => FactsFor(pluginFileName).Description;

    private PluginFacts FactsFor(string name)
    {
        if (_facts.TryGetValue(name, out var known)) return known;

        return _facts[name] = ReadFromDisk(name);
    }

    private PluginFacts ReadFromDisk(string name)
    {
        // A plugin is a bare file name in the Data folder; a path, or a pipe or device in its place, is not one.
        if (!ConditionPaths.IsPlainFileName(name)) return default;

        var path = ConditionPaths.Resolve(DataDirectory, name, ConditionPaths.ShouldTryGhost(Game, name));
        try
        {
            if (path is null) return default;

            using var stream = ConditionPaths.OpenRegularFile(path);
            if (stream is null) return default;

            var header = PluginHeaderReader.Read(stream, _game.HeaderFormat, name);
            return new PluginFacts(_game.IsMaster(header, _installation), header.Description);
        }
        catch (Exception e) when (e is InvalidPluginException or IOException or UnauthorizedAccessException)
        {
            // A plugin that cannot be read is not a master and has no description, as for LOOT itself.
            return default;
        }
    }

    private readonly record struct PluginFacts(bool IsMaster, string? Description);
}
