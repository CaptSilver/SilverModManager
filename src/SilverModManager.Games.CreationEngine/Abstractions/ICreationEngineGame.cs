using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.Bethesda.LoadOrder;
using SilverModManager.Games.Bethesda.Loot.Masterlists;
using SilverModManager.Games.CreationEngine.PluginLists;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;


using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.CreationEngine.Abstractions;

/// <summary>
/// A Bethesda game whose plugin headers the load order reader can parse.
/// </summary>
public interface ICreationEngineGame : IBethesdaGame
{
    public GamePath PluginsFile { get; }

    /// <summary>The header layout this game's plugins use.</summary>
    public PluginFormat HeaderFormat { get; }

    /// <summary>
    /// How this game keeps its load order. Takes the installation because a title may keep it in different
    /// places depending on how it is installed.
    /// </summary>
    public IPluginListMechanism PluginList(GameInstallation installation);

    /// <summary>
    /// Which LOOT masterlist applies to this install. Takes the installation because Morrowind sorts as OpenMW
    /// when the install uses it.
    /// </summary>
    public LootGame LootGame(GameInstallation installation);

    /// <summary>Plugins the game loads whether or not any list names them, in load order.</summary>
    public IReadOnlyList<string> HardcodedPlugins { get; }

    /// <summary>The Creation Club list file, when the game has one. See <see cref="EarlyLoadingPlugins"/>.</summary>
    public GamePath? CreationClubListFile { get; }

    /// <summary>
    /// Finds the content the game installs into its own folder, or null when the game has no record of it. Its
    /// <see cref="IGameInstalledContentRecognizer.RecordPaths"/> are also kept out of the sync.
    /// </summary>
    public IGameInstalledContentRecognizer? GameInstalledContent { get; }

    /// <summary>Ini files that can set <c>sTestFile1</c> to <c>sTestFile10</c>, ordered so that a later file overrides an earlier one.</summary>
    public IReadOnlyList<GamePath> TestFileIniFiles { get; }

    /// <summary>Plugin file extensions the game loads.</summary>
    public Extension[] PluginExtensions { get; }

    public bool SupportsLightPlugins { get; }

    /// <summary>The game treats .esm and .esl files as masters whatever their flags say.</summary>
    public bool MasterByExtension { get; }

    /// <summary>The game never reads a master flag.</summary>
    public bool MasterFlagIgnored(GameInstallation installation);

    /// <summary>
    /// Plugins the game loads whether or not a list switches them on, because of other files in Data. Takes the
    /// names of the files in Data and returns the plugin names they activate.
    /// </summary>
    public IReadOnlyList<string> ImplicitlyActivePlugins(IEnumerable<string> dataFileNames);

    public bool IsMaster(PluginHeader header, GameInstallation installation);
    public bool IsLight(PluginHeader header);
    public bool IsMedium(PluginHeader header);
    public bool IsUpdate(PluginHeader header);
    public bool IsBlueprint(PluginHeader header);

    /// <summary>
    /// Plugins the game loads before any list: the hardcoded ones, then the Creation Club list. Case-insensitive
    /// duplicates are dropped, keeping the first.
    /// </summary>
    public IReadOnlyList<string> EarlyLoadingPlugins(GameInstallation installation);

    /// <summary>
    /// Parse a plugin file. Reads only the header record of the file: its masters, flags and version
    /// </summary>
    /// <returns>
    /// The plugin header, or <c>null</c> when no stream source can supply the bytes for <paramref name="hash"/>.
    /// That happens for a game file whose recorded build is no longer on disk.
    /// </returns>
    public ValueTask<PluginHeader?> ParsePlugin(Hash hash, RelativePath? name = null);
}
