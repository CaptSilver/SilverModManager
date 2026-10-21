using System.Collections.Immutable;
using DynamicData.Kernel;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Diagnostics.Emitters;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Games.CreationEngine.Abstractions;
using NexusMods.Games.CreationEngine.Morrowind.OpenMw;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Games.CreationEngine.Morrowind;

/// <summary>
/// Morrowind keeps everything in the game folder: mods go into "Data Files", and Morrowind.ini and Saves sit
/// next to the executable. Its plugin list is the [Game Files] section of Morrowind.ini; no reader for that
/// exists yet, so Apply does not write that list.
/// </summary>
/// <remarks>
/// OpenMW reads its load order from an openmw.cfg on the host. When one is found, its folder becomes the
/// <see cref="OpenMwConfig"/> location and Apply rewrites the <c>content=</c> lines it owns (see
/// <see cref="OpenMwConfigFile"/>); nothing else under that folder is indexed.
/// </remarks>
public class Morrowind(IServiceProvider provider) : ABethesdaGame<Morrowind>(provider), IGameData<Morrowind>
{
    /// <summary>The folder that holds the user's openmw.cfg, present only when exactly one is found.</summary>
    public static readonly LocationId OpenMwConfig = LocationId.From("OpenMwConfig");

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

    public override Dictionary<GamePath, IIntrinsicFile> IntrinsicFiles(Loadout.ReadOnly loadout, IServiceProvider provider)
    {
        var files = base.IntrinsicFiles(loadout, provider);
        if (!loadout.IsValid() || !loadout.InstallationInstance.Locations.ContainsKey(OpenMwConfig)) return files;

        var config = new OpenMwConfigFile(this);
        files[config.Path] = config;
        return files;
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
