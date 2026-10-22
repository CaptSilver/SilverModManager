using DynamicData.Kernel;
using NexusMods.Games.Bethesda.LoadOrder;
using NexusMods.Games.Bethesda.Loot.Masterlists;
using NexusMods.Games.CreationEngine.Abstractions;
using NexusMods.Games.CreationEngine.PluginLists;
using NexusMods.Paths;
using NexusMods.Sdk.Games;

namespace NexusMods.Games.CreationEngine.Starfield;

/// <summary>
/// Starfield keeps a star-format Plugins.txt in AppData, and its plugins use the TES4 header layout with its own
/// flag bits (light, medium, update, blueprint). Loose files also need StarfieldCustom.ini to be loaded;
/// writing that is not done yet.
/// </summary>
public class Starfield(IServiceProvider provider) : ACreationEngineGame<Starfield>(provider), IGameData<Starfield>
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
    public override PluginFormat HeaderFormat => PluginFormat.Tes4;
    public override LootGame LootGame(GameInstallation installation) => Bethesda.Loot.Masterlists.LootGame.Starfield;
    public override GamePath PluginsFile => new(LocationId.AppData, "Plugins.txt");
    public override IPluginListMechanism PluginList(GameInstallation installation) => Mechanism;
    private static readonly IPluginListMechanism Mechanism = new AsteriskMechanism();

    // libloadorder game_settings.rs STARFIELD_HARDCODED_PLUGINS.
    public override IReadOnlyList<string> HardcodedPlugins =>
    [
        "Starfield.esm", "Constellation.esm", "OldMars.esm", "ShatteredSpace.esm", "SFBGS003.esm", "SFBGS004.esm",
        "SFBGS006.esm", "SFBGS007.esm", "SFBGS008.esm", "SFBGS00D.esm", "SFBGS047.esm", "SFBGS050.esm",
    ];

    public override bool SupportsLightPlugins => true;
    public override bool MasterByExtension => true;
    protected override bool IsStarfield => true;

    // The My Games list overrides the one in the game folder, even when it is empty (game_settings.rs ccc_file_paths).
    public override GamePath? CreationClubListFile => new(LocationId.Preferences, "Starfield.ccc");
    protected override GamePath? FallbackCreationClubListFile => new(LocationId.Game, "Starfield.ccc");

    public override IReadOnlyList<GamePath> TestFileIniFiles =>
        [new(LocationId.Game, "Starfield.ini"), new(LocationId.Preferences, "StarfieldCustom.ini")];

    // https://github.com/ModOrganizer2/modorganizer-game_starfield/blob/master/src/gamestarfield.cpp
    // (determineMyGamesPath("Starfield") and the plugins.txt copy from localAppFolder() + "/Starfield")
    protected override string LocalAppDataFolderName(GameLocatorResult gameLocatorResult) => "Starfield";
    protected override string MyGamesFolderName(GameLocatorResult gameLocatorResult) => "Starfield";

    protected override string[] GameAliases => ["Starfield", "SF"];
    protected override string[] StopPatterns => ["(^|/)sfse(/|$)"];
    protected override string[] EngineFiles => [@"sfse_loader\.exe", @"sfse_.*\.dll"];
}
