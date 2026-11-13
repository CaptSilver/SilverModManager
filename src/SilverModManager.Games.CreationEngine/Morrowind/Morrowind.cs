using System.Collections.Immutable;
using DynamicData.Kernel;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Ini;
using SilverModManager.Games.Bethesda.LoadOrder;
using SilverModManager.Games.Bethesda.LoadOrder.Formats;
using SilverModManager.Games.CreationEngine.SortOrder;
using SilverModManager.Sdk.Ini;
using SilverModManager.Games.Bethesda.Loot.Masterlists;
using SilverModManager.Games.CreationEngine.Abstractions;
using SilverModManager.Games.CreationEngine.OpenMw;
using SilverModManager.Games.CreationEngine.PluginLists;
using NexusMods.Hashing.xxHash3;
using SilverModManager.Sdk.Hashes;
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
/// An openmw.cfg on the host is OpenMW's, which has its own game entry. The config folder is still registered as
/// an inert <see cref="OpenMwConstants.OpenMwConfig"/> location so paths that loadouts of an earlier version stored
/// under it keep resolving, and the row never writes anything there (<see cref="MorrowindSynchronizer"/>).
/// </remarks>
public class Morrowind(IServiceProvider provider) : ACreationEngineGame<Morrowind>(provider), IGameData<Morrowind>, IHasIniFiles, ITimestampOrderedGame
{
    /// <inheritdoc />
    /// <remarks>The inert OpenMW config location is named where OpenMW's own entry names it.</remarks>
    public IReadOnlyDictionary<string, string> LocationNames => OpenMw.OpenMw.OpenMwLocationNames;

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

    public override LootGame LootGame(GameInstallation installation) => Bethesda.Loot.Masterlists.LootGame.Morrowind;

    public override IPluginListMechanism PluginList(GameInstallation installation) => Ini;

    private static readonly MorrowindIniMechanism Ini = new();

    // libloadorder game_settings.rs hardcoded_plugins: none for Morrowind.
    public override IReadOnlyList<string> HardcodedPlugins => [];

    // OpenMW's plugin and Lua script formats load like .esp.
    public override Extension[] PluginExtensions =>
    [
        KnownCEExtensions.ESM, KnownCEExtensions.ESP,
        KnownCEExtensions.OMWAddon, KnownCEExtensions.OMWGame, KnownCEExtensions.OMWScripts,
    ];

    // A .omwscripts file is a text list of Lua scripts, with no header record to read.
    public override ValueTask<PluginHeader?> ParsePlugin(FileIdentity identity, RelativePath? name = null) =>
        name is { } file && file.Extension == KnownCEExtensions.OMWScripts
            ? ValueTask.FromResult<PluginHeader?>(ScriptsHeader(file))
            : base.ParsePlugin(identity, name);

    public override ValueTask<PluginHeader?> ParsePluginFile(AbsolutePath file, RelativePath? name = null) =>
        name is { } plugin && plugin.Extension == KnownCEExtensions.OMWScripts
            ? ValueTask.FromResult<PluginHeader?>(ScriptsHeader(plugin))
            : base.ParsePluginFile(file, name);

    private PluginHeader ScriptsHeader(RelativePath file) => new(file.FileName.ToString(), [], 0, null, null, null, null, HeaderFormat);

    public Task<IReadOnlyList<(GamePath Path, DateTimeOffset Mtime)>> PluginTimestamps(Loadout.ReadOnly loadout, IReadOnlyDictionary<GamePath, SyncNode> syncTree) =>
        PlanPluginTimestamps(loadout, syncTree);

    // Morrowind.ini is the loadout's own copy like every other owned INI; the active plugins are rendered into its
    // [Game Files] section from the plugin order.
    // Active, not just switched on: a plugin of a disabled mod keeps its row's switch but is not deployed.
    protected override Task RenderDerivedIniKeys(IniDocument ini, GamePath path, Loadout.ReadOnly loadout)
    {
        if (path != PluginsFile) return Task.CompletedTask;

        var variety = SortOrderManager.GetSortOrderVarieties().OfType<IPluginSortOrderVariety>().Single();
        var active = variety.GetSortOrderIdFor(loadout.LoadoutId, loadout.Db) is { HasValue: true } sortOrderId
            ? variety.GetSortOrderItems(sortOrderId.Value, loadout.Db).Where(item => item.IsActive).Select(item => item.DisplayName).ToList()
            : [];
        MorrowindIniGameFiles.Apply(ini, active);
        return Task.CompletedTask;
    }

    protected override bool IsDerivedIniKey(GamePath path, string section, string key) =>
        path == PluginsFile && MorrowindIniGameFiles.IsGameFileKey(section, key);

    // Morrowind has no archive invalidation switch in its INI.
    protected override IReadOnlyList<IniKeyEdit> ArchiveIniKeys => [];

    protected override RelativePath PrimaryExe => "Morrowind.exe";

    /// <inheritdoc />
    protected override string[] SaveExtensions => [".ess"];

    /// <inheritdoc />
    protected override string? SaveRedirectUnsupportedReason => "Morrowind has no save-path setting";

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
            ? locations.Add(OpenMwConstants.OpenMwConfig, one.Folder)
            : locations;
    }

    protected override IEnumerable<IDiagnosticEmitter> CreateEmitters(IServiceProvider provider) =>
    [
        new MorrowindOpenMwRetiredEmitter(this),
        .. base.CreateEmitters(provider),
    ];

    protected override ILoadoutSynchronizer CreateSynchronizer(IServiceProvider provider) => new MorrowindSynchronizer(provider, this);

    // OpenMW's folder belongs to OpenMW and its own row: nothing in it is indexed by this one.
    public override bool IsProtectedPath(GamePath path) =>
        base.IsProtectedPath(path) || path.LocationId == OpenMwConstants.OpenMwConfig;
}
