using SilverModManager.Abstractions.Games;
using SilverModManager.Games.Bethesda.Loot.Masterlists;
using DynamicData.Kernel;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.CreationEngine.Oblivion;

public class Oblivion(IServiceProvider provider) : AOblivionGame<Oblivion>(provider), IGameData<Oblivion>
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

    public override LootGame LootGame(GameInstallation installation) => Bethesda.Loot.Masterlists.LootGame.Oblivion;

    // https://github.com/ModOrganizer2/modorganizer-game_oblivion/blob/master/src/gameoblivion.cpp
    // (the plugins.txt copy from localAppFolder() + "/Oblivion", and gameName() is "Oblivion")
    protected override string LocalAppDataFolderName(GameLocatorResult gameLocatorResult) => "Oblivion";
    protected override string MyGamesFolderName(GameLocatorResult gameLocatorResult) => "Oblivion";

    protected override string[] GameAliases => ["Oblivion", "TES4"];
}
