using DynamicData.Kernel;
using NexusMods.Games.CreationEngine.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk.Games;

namespace NexusMods.Games.CreationEngine.Morrowind;

/// <summary>
/// Morrowind keeps everything in the game folder: mods go into "Data Files", and Morrowind.ini and Saves sit
/// next to the executable. Its plugin list is the [Game Files] section of Morrowind.ini; no reader for that
/// exists yet, so Apply round-trips plain files and does not write the list.
/// </summary>
public class Morrowind(IServiceProvider provider) : ABethesdaGame<Morrowind>(provider), IGameData<Morrowind>
{
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
}
