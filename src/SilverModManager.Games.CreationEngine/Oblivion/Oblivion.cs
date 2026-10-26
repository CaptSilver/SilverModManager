using SilverModManager.Abstractions.Games;
using DynamicData.Kernel;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.Bethesda.LoadOrder;
using SilverModManager.Games.Bethesda.Loot.Masterlists;
using SilverModManager.Games.CreationEngine.PluginLists;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Games.CreationEngine.Abstractions;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.CreationEngine.Oblivion;

/// <summary>
/// Oblivion orders its plugins by file modification time and reads the active ones from Plugins.txt, which sits in
/// AppData unless Oblivion.ini sets <c>bUseMyGamesDirectory=0</c>, and then in the game folder. The launcher writes
/// plugins.txt and the game reads Plugins.txt; they are one file on the prefix's case-insensitive view.
/// </summary>
public class Oblivion(IServiceProvider provider) : ACreationEngineGame<Oblivion>(provider), IGameData<Oblivion>, IHasIniFiles, ITimestampOrderedGame
{
    public static GameId GameId { get; } = GameId.From("CreationEngine.Oblivion");
    public static string DisplayName => "Oblivion";
    public static Optional<Sdk.NexusModsApi.NexusModsGameId> NexusModsGameId => Sdk.NexusModsApi.NexusModsGameId.From(101);

    public override StoreIdentifiers StoreIdentifiers { get; } = new(GameId)
    {
        // 22330: https://github.com/ModOrganizer2/modorganizer-game_oblivion/blob/master/src/gameoblivion.cpp (steamAPPId)
        // 900883: the Game of the Year Deluxe edition is its own Steam app, https://store.steampowered.com/api/appdetails?appids=900883
        SteamAppIds = [22330u, 900883u],
        // https://www.gogdb.org/product/1458058109 (Game of the Year Edition Deluxe)
        GOGProductIds = [1458058109L],
    };

    public override PluginFormat HeaderFormat => PluginFormat.Tes4Oblivion;
    public override LootGame LootGame(GameInstallation installation) => Bethesda.Loot.Masterlists.LootGame.Oblivion;
    public override GamePath PluginsFile => new(LocationId.AppData, "Plugins.txt");
    public override IPluginListMechanism PluginList(GameInstallation installation) => Mechanism;
    private static readonly TimestampMechanism Mechanism = new(locationFollowsIni: true);
    // libloadorder game_settings.rs hardcoded_plugins: none for these games.
    public override IReadOnlyList<string> HardcodedPlugins => [];

    public Task<IReadOnlyList<(GamePath Path, DateTimeOffset Mtime)>> PluginTimestamps(Loadout.ReadOnly loadout, IReadOnlyDictionary<GamePath, SyncNode> syncTree) =>
        PlanPluginTimestamps(loadout, syncTree);

    protected override RelativePath PrimaryExe => "Oblivion.exe";

    /// <inheritdoc />
    protected override string[] SaveExtensions => [".ess", ".obse"];

    // The game only reads sLocalSavePath once it looks in My Games at all.
    protected override bool SaveRedirectNeedsMyGamesDirectory => true;

    // https://github.com/ModOrganizer2/modorganizer-game_oblivion/blob/master/src/gameoblivion.cpp
    // (the plugins.txt copy from localAppFolder() + "/Oblivion", and gameName() is "Oblivion")
    protected override string LocalAppDataFolderName(GameLocatorResult gameLocatorResult) => "Oblivion";
    protected override string MyGamesFolderName(GameLocatorResult gameLocatorResult) => "Oblivion";

    protected override string[] GameAliases => ["Oblivion", "TES4"];
    protected override string[] StopPatterns => ["(^|/)obse(/|$)"];
    protected override string[] EngineFiles =>
    [
        @"obse_loader\.exe",
        @"obse_1_2_416\.dll",
        @"obse_steam_loader\.dll",
        @"obse_editor_1_2\.dll",
    ];
}
