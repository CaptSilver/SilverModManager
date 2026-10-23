using System.Collections.Immutable;
using DynamicData.Kernel;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.Bethesda.LoadOrder;
using SilverModManager.Games.Bethesda.Loot.Masterlists;
using SilverModManager.Games.CreationEngine.Abstractions;
using SilverModManager.Games.CreationEngine.Morrowind.OpenMw;
using SilverModManager.Games.CreationEngine.PluginLists;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.CreationEngine.Morrowind;

/// <summary>
/// Morrowind keeps everything in the game folder: mods go into "Data Files", and Morrowind.ini and Saves sit
/// next to the executable. Its plugin list is the [Game Files] section of Morrowind.ini, and the game loads the
/// plugins in the order of their file times.
/// </summary>
/// <remarks>
/// OpenMW reads its load order from an openmw.cfg on the host. When one is found, its folder becomes the
/// <see cref="OpenMwConfig"/> location, openmw.cfg decides the order the app starts from, and Apply rewrites
/// the <c>content=</c> lines it owns in the app's order (see <see cref="OpenMwMechanism"/>); nothing else under
/// that folder is indexed. Morrowind.ini is written either way.
/// </remarks>
public class Morrowind(IServiceProvider provider) : ACreationEngineGame<Morrowind>(provider), IGameData<Morrowind>, ITimestampOrderedGame
{
    /// <summary>The folder that holds the user's openmw.cfg, present only when exactly one is found.</summary>
    public static readonly LocationId OpenMwConfig = LocationId.From("OpenMwConfig");

    public static readonly GamePath OpenMwConfigFile = new(OpenMwConfig, OpenMwConfigLocation.FileName);

    private readonly IOpenMwConfigRoots _openMwConfigRoots = provider.GetRequiredService<IOpenMwConfigRoots>();

    public static GameId GameId { get; } = GameId.From("CreationEngine.Morrowind");
    public static string DisplayName => "Morrowind";
    public static Optional<Sdk.NexusModsApi.NexusModsGameId> NexusModsGameId => Sdk.NexusModsApi.NexusModsGameId.From(100);

    public override StoreIdentifiers StoreIdentifiers { get; } = new(GameId)
    {
        SteamAppIds = [22320u],
        // https://www.gogdb.org/product/1435828767 (The Elder Scrolls III: Morrowind GOTY Edition)
        GOGProductIds = [1435828767L],
    };

    public override PluginFormat HeaderFormat => PluginFormat.Tes3;
    public override GamePath PluginsFile => new(LocationId.Game, "Morrowind.ini");

    // OpenMW has no masterlist of its own; it shares Morrowind's, but sorts without the master flag.
    public override LootGame LootGame(GameInstallation installation) =>
        installation.Locations.ContainsKey(OpenMwConfig) ? Bethesda.Loot.Masterlists.LootGame.OpenMW : Bethesda.Loot.Masterlists.LootGame.Morrowind;

    public override IPluginListMechanism PluginList(GameInstallation installation) =>
        installation.Locations.ContainsKey(OpenMwConfig) ? OpenMw : Ini;

    private static readonly MorrowindIniMechanism Ini = new();
    private static readonly OpenMwMechanism OpenMw = new();

    // libloadorder game_settings.rs hardcoded_plugins: none for Morrowind. OpenMW's early loaders come from its config.
    public override IReadOnlyList<string> HardcodedPlugins => [];

    // OpenMW loads plugins in the order of its content= lines and ignores the master flag
    // (libloot plugin/mod.rs is_master_file), so the flag only counts for the original game.
    public override bool MasterFlagIgnored(GameInstallation installation) => installation.Locations.ContainsKey(OpenMwConfig);

    /// <summary>
    /// With OpenMW, <c>builtin.omwscripts</c>, which OpenMW always loads first (libloadorder game_settings.rs,
    /// early_loading_plugins).
    /// </summary>
    /// <remarks>
    /// libloadorder also counts the content of the configs loaded before the user's openmw.cfg, chiefly the
    /// system-wide one. The app only knows where the user's own file is, so that content is not read. A config the
    /// user's file chains to with <c>config=</c> loads after it, so its content is not early either.
    /// </remarks>
    public override IReadOnlyList<string> EarlyLoadingPlugins(GameInstallation installation)
    {
        var early = base.EarlyLoadingPlugins(installation);
        if (!installation.Locations.ContainsKey(OpenMwConfig)) return early;

        return early.Append(BuiltInScripts).Distinct(PluginNameComparer.Instance).ToList();
    }

    private const string BuiltInScripts = "builtin.omwscripts";

    // OpenMW's plugin and Lua script formats load like .esp.
    public override Extension[] PluginExtensions =>
    [
        KnownCEExtensions.ESM, KnownCEExtensions.ESP,
        KnownCEExtensions.OMWAddon, KnownCEExtensions.OMWGame, KnownCEExtensions.OMWScripts,
    ];

    // A .omwscripts file is a text list of Lua scripts, with no header record to read.
    public override ValueTask<PluginHeader?> ParsePlugin(Hash hash, RelativePath? name = null) =>
        name is { } file && file.Extension == KnownCEExtensions.OMWScripts
            ? ValueTask.FromResult<PluginHeader?>(new PluginHeader(file.FileName.ToString(), [], 0, null, null, null, null, HeaderFormat))
            : base.ParsePlugin(hash, name);

    public Task<IReadOnlyList<(GamePath Path, DateTimeOffset Mtime)>> PluginTimestamps(Loadout.ReadOnly loadout, IReadOnlyDictionary<GamePath, SyncNode> syncTree) =>
        PlanPluginTimestamps(loadout, syncTree);

    protected override RelativePath PrimaryExe => "Morrowind.exe";

    protected override string DataFolderName => "Data Files";

    // Saves live in the game folder, not under My Games.
    public override IReadOnlyList<GamePath> ProtectedFolders => [new GamePath(LocationId.Game, "Saves")];

    protected override string[] GameAliases => ["Morrowind", "MW", "Morrowind GOTY"];

    // MWSE loads through MGE XE's d3d8.dll proxy when Morrowind.exe starts, so there is no loader to launch.
    protected override string[] StopPatterns => ["(^|/)mwse(/|$)"];
    protected override string[] EngineFiles =>
    [
        @"MWSE\.dll",
        @"MWSE-Update\.exe",
        @"MGEXEgui\.exe",
        @"d3d8\.dll",
        @"MGEfuncs\.dll",
    ];

    protected override string[] TopLevelDirs => [.. KnownPaths.CommonTopLevelFolders, "icons", "bookart", "splash", "mwse"];

    // OpenMW's plugin and Lua script formats route like .esp.
    protected override Extension[] PluginLikeExtensions =>
    [
        KnownCEExtensions.ESM, KnownCEExtensions.ESP, KnownCEExtensions.ESL,
        KnownCEExtensions.OMWAddon, KnownCEExtensions.OMWGame, KnownCEExtensions.OMWScripts,
    ];

    public override ImmutableDictionary<LocationId, AbsolutePath> GetLocations(IFileSystem fileSystem, GameLocatorResult gameLocatorResult)
    {
        var locations = base.GetLocations(fileSystem, gameLocatorResult);
        return OpenMwConfigLocation.Locate(_openMwConfigRoots, fileSystem) is OpenMwConfigLocation.One one
            ? locations.Add(OpenMwConfig, one.Folder)
            : locations;
    }

    protected override IEnumerable<IDiagnosticEmitter> CreateEmitters(IServiceProvider provider) =>
    [
        new OpenMwDiagnosticEmitter(provider.GetRequiredService<IOpenMwConfigRoots>(), provider.GetRequiredService<IFileSystem>()),
        .. base.CreateEmitters(provider),
    ];

    // Only openmw.cfg is managed; settings.cfg, launcher.cfg and the rest of OpenMW's folder are the user's.
    public override bool IsProtectedPath(GamePath path) =>
        base.IsProtectedPath(path)
        || (path.LocationId == OpenMwConfig && path.Path != OpenMwConfigLocation.FileName);
}
