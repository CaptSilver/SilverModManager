using DynamicData.Kernel;
using NexusMods.Games.CreationEngine.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk.Games;

namespace NexusMods.Games.CreationEngine.Oblivion;

/// <summary>
/// Oblivion keeps a plain plugins.txt in AppData, but no plugin header reader exists for it yet, so this game
/// cannot order masters and does not write the plugin list. Apply round-trips plain files.
/// </summary>
public class Oblivion(IServiceProvider provider) : ABethesdaGame<Oblivion>(provider), IGameData<Oblivion>
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

    protected override RelativePath PrimaryExe => "Oblivion.exe";

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
