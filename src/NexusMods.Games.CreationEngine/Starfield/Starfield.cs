using DynamicData.Kernel;
using NexusMods.Games.CreationEngine.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk.Games;

namespace NexusMods.Games.CreationEngine.Starfield;

/// <summary>
/// Starfield keeps a star-format Plugins.txt in AppData, but Mutagen.Bethesda.Starfield is not referenced,
/// so this game cannot read plugin headers and writes no plugin list. Loose files also need
/// StarfieldCustom.ini to be loaded; writing that is not done yet either.
/// </summary>
public class Starfield(IServiceProvider provider) : ABethesdaGame<Starfield>(provider), IGameData<Starfield>
{
    public static GameId GameId { get; } = GameId.From("CreationEngine.Starfield");
    public static string DisplayName => "Starfield";
    public static Optional<Sdk.NexusModsApi.NexusModsGameId> NexusModsGameId => Sdk.NexusModsApi.NexusModsGameId.From(4187);

    public override StoreIdentifiers StoreIdentifiers { get; } = new(GameId)
    {
        // https://github.com/ModOrganizer2/modorganizer-game_starfield/blob/master/src/gamestarfield.cpp (steamAPPId)
        SteamAppIds = [1716740u],
    };

    protected override RelativePath PrimaryExe => "Starfield.exe";

    // https://github.com/ModOrganizer2/modorganizer-game_starfield/blob/master/src/gamestarfield.cpp
    // (determineMyGamesPath("Starfield") and the plugins.txt copy from localAppFolder() + "/Starfield")
    protected override string LocalAppDataFolderName(GameLocatorResult gameLocatorResult) => "Starfield";
    protected override string MyGamesFolderName(GameLocatorResult gameLocatorResult) => "Starfield";

    protected override string[] GameAliases => ["Starfield", "SF"];
    protected override string[] StopPatterns => ["(^|/)sfse(/|$)"];
    protected override string[] EngineFiles => [@"sfse_loader\.exe", @"sfse_.*\.dll"];
}
