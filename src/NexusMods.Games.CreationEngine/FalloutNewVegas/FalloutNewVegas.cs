using DynamicData.Kernel;
using NexusMods.Games.CreationEngine.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk.Games;

namespace NexusMods.Games.CreationEngine.FalloutNewVegas;

/// <summary>
/// New Vegas keeps a plain plugins.txt in AppData, but no plugin header reader exists for it yet, so this game
/// cannot order masters and does not write the plugin list. Apply round-trips plain files. Tale of Two
/// Wastelands is a mod with its own installer, not a separate game.
/// </summary>
public class FalloutNewVegas(IServiceProvider provider) : ABethesdaGame<FalloutNewVegas>(provider), IGameData<FalloutNewVegas>
{
    public static GameId GameId { get; } = GameId.From("CreationEngine.FalloutNewVegas");
    public static string DisplayName => "Fallout: New Vegas";
    // The Nexus domain is "newvegas".
    public static Optional<Sdk.NexusModsApi.NexusModsGameId> NexusModsGameId => Sdk.NexusModsApi.NexusModsGameId.From(130);

    public override StoreIdentifiers StoreIdentifiers { get; } = new(GameId)
    {
        // 22380, and 22490 for the English/Polish/Czech/Russian release (installed under a folder ending in "enplczru"):
        // https://github.com/ModOrganizer2/modorganizer-game_falloutnv/blob/master/src/gamefalloutnv.cpp (steamAPPId)
        SteamAppIds = [22380u, 22490u],
        // https://www.gogdb.org/product/1454587428 (Ultimate Edition)
        GOGProductIds = [1454587428L],
    };

    protected override RelativePath PrimaryExe => "FalloutNV.exe";

    // https://github.com/ModOrganizer2/modorganizer-game_falloutnv/blob/master/src/gamefalloutnv.cpp
    // (gameDirectoryName() is "FalloutNV" outside the Epic release; it names both the My Games folder
    // and the plugins.txt copy from localAppFolder())
    protected override string LocalAppDataFolderName(GameLocatorResult gameLocatorResult) => "FalloutNV";
    protected override string MyGamesFolderName(GameLocatorResult gameLocatorResult) => "FalloutNV";

    protected override string[] GameAliases => ["Fallout New Vegas", "Fallout: New Vegas", "FalloutNV", "FNV"];
    protected override string[] StopPatterns => ["(^|/)nvse(/|$)"];
    protected override string[] EngineFiles => [@"nvse_loader\.exe", @"nvse_.*\.dll", @"nvse_steam_loader\.dll"];
}
