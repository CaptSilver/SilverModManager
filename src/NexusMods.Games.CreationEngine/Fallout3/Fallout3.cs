using DynamicData.Kernel;
using NexusMods.Games.CreationEngine.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk.Games;

namespace NexusMods.Games.CreationEngine.Fallout3;

/// <summary>
/// Fallout 3 keeps a plain plugins.txt in AppData, but no plugin header reader exists for it yet, so this game
/// cannot order masters and does not write the plugin list. Apply round-trips plain files.
/// </summary>
public class Fallout3(IServiceProvider provider) : ABethesdaGame<Fallout3>(provider), IGameData<Fallout3>
{
    public static GameId GameId { get; } = GameId.From("CreationEngine.Fallout3");
    public static string DisplayName => "Fallout 3";
    public static Optional<Sdk.NexusModsApi.NexusModsGameId> NexusModsGameId => Sdk.NexusModsApi.NexusModsGameId.From(120);

    public override StoreIdentifiers StoreIdentifiers { get; } = new(GameId)
    {
        // 22300 (regular) and 22370 (Game of the Year):
        // https://github.com/ModOrganizer2/modorganizer-game_fallout3/blob/master/src/gamefallout3.cpp (steamAPPId)
        SteamAppIds = [22300u, 22370u],
        // https://www.gogdb.org/product/1454315831 (Game of the Year Edition)
        GOGProductIds = [1454315831L],
    };

    protected override RelativePath PrimaryExe => "Fallout3.exe";

    // https://github.com/ModOrganizer2/modorganizer-game_fallout3/blob/master/src/gamefallout3.cpp
    // (determineMyGamesPath("Fallout3") and the plugins.txt copy from localAppFolder() + "/Fallout3")
    protected override string LocalAppDataFolderName(GameLocatorResult gameLocatorResult) => "Fallout3";
    protected override string MyGamesFolderName(GameLocatorResult gameLocatorResult) => "Fallout3";

    protected override string[] GameAliases => ["Fallout 3", "Fallout3", "FO3"];
    protected override string[] StopPatterns => ["(^|/)fose(/|$)"];
    protected override string[] EngineFiles => [@"fose_loader\.exe", @"fose_.*\.dll"];
}
