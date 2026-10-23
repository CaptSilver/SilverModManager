using DynamicData.Kernel;
using SilverModManager.Games.CreationEngine.Abstractions;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.CreationEngine.Fallout76;

/// <summary>
/// Fallout 76 has no plugin list: mods are .ba2 archives that the game loads once they are named in
/// Fallout76Custom.ini. Writing that file is not done yet, so an installed archive is deployed but not enabled.
/// </summary>
public class Fallout76(IServiceProvider provider) : ABethesdaGame<Fallout76>(provider), IGameData<Fallout76>
{
    public static GameId GameId { get; } = GameId.From("CreationEngine.Fallout76");
    public static string DisplayName => "Fallout 76";
    public static Optional<Sdk.NexusModsApi.NexusModsGameId> NexusModsGameId => Sdk.NexusModsApi.NexusModsGameId.From(2590);

    public override StoreIdentifiers StoreIdentifiers { get; } = new(GameId)
    {
        // https://github.com/ModOrganizer2/modorganizer-game_fallout76/blob/master/src/gamefallout76.cpp (steamAPPId)
        SteamAppIds = [1151340u],
    };

    protected override RelativePath PrimaryExe => "Fallout76.exe";

    // https://github.com/ModOrganizer2/modorganizer-game_fallout76/blob/master/src/gamefallout76.cpp
    // (determineMyGamesPath(gameName()), and gameName() is "Fallout 76")
    protected override string MyGamesFolderName(GameLocatorResult gameLocatorResult) => "Fallout 76";

    protected override string[] GameAliases => ["Fallout 76", "Fallout76", "FO76"];
}
