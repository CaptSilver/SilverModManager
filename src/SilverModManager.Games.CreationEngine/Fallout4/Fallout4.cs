using DynamicData.Kernel;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.Bethesda.LoadOrder;
using SilverModManager.Games.Bethesda.Loot.Masterlists;
using SilverModManager.Games.CreationEngine.Abstractions;
using SilverModManager.Games.CreationEngine.Creations;
using SilverModManager.Games.CreationEngine.PluginLists;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.CreationEngine.Fallout4;

public class Fallout4(IServiceProvider provider) : ACreationEngineGame<Fallout4>(provider), IGameData<Fallout4>
{
    public static GameId GameId { get; } = GameId.From("CreationEngine.Fallout4");
    public static string DisplayName => "Fallout 4";
    public static Optional<Sdk.NexusModsApi.NexusModsGameId> NexusModsGameId => Sdk.NexusModsApi.NexusModsGameId.From(1151);

    public override StoreIdentifiers StoreIdentifiers { get; } = new(GameId)
    {
        SteamAppIds = [377160u],
        GOGProductIds = [1998527297L],
    };

    protected override RelativePath PrimaryExe => "Fallout4.exe";
    public override PluginFormat HeaderFormat => PluginFormat.Tes4;
    public override LootGame LootGame(GameInstallation installation) => Bethesda.Loot.Masterlists.LootGame.Fallout4;
    public override GamePath PluginsFile => Fallout4KnownPaths.PluginsFile;
    public override IPluginListMechanism PluginList(GameInstallation installation) => Mechanism;
    private static readonly IPluginListMechanism Mechanism = new AsteriskMechanism();
    public override IReadOnlyList<string> HardcodedPlugins => [
        "Fallout4.esm",
        "DLCRobot.esm",
        "DLCworkshop01.esm",
        "DLCCoast.esm",
        "DLCworkshop02.esm",
        "DLCworkshop03.esm",
        "DLCNukaWorld.esm",
        "DLCUltraHighResolution.esm",
    ];
    public override bool SupportsLightPlugins => true;
    public override bool MasterByExtension => true;
    public override GamePath? CreationClubListFile => new(LocationId.Game, "Fallout4.ccc");
    public override IReadOnlyList<GamePath> TestFileIniFiles => [new(LocationId.Preferences, "Fallout4.ini"), new(LocationId.Preferences, "Fallout4Custom.ini")];

    // Not checked against an installed Fallout 4: the catalog and manifest locations are taken from Skyrim
    // Special Edition, which shares the format.
    private readonly CreationsCatalogRecognizer _gameInstalledContent = new CreationsCatalogRecognizer(
        new GamePath(LocationId.AppData, "ContentCatalog.txt"),
        new GamePath(LocationId.Game, "Creations"),
        new GamePath(LocationId.Game, "Data"),
        "Fallout4Creation");

    protected override IGameInstalledContentRecognizer? GameInstalledContent => _gameInstalledContent;

    protected override string LocalAppDataFolderName(GameLocatorResult gameLocatorResult) => "Fallout4";
    protected override string MyGamesFolderName(GameLocatorResult gameLocatorResult) => "Fallout4";

    protected override string[] GameAliases => ["Fallout 4", "Fallout4", "FO4", "F4"];
    protected override string[] StopPatterns => ["(^|/)f4se(/|$)"];
    protected override string[] EngineFiles =>
    [
        // F4SE
        @"f4se_loader\.exe",
        @"f4se_.*\.dll",
        // Plugin Preloader (new)
        @"winhttp\.dll",
        @"xSE\ PluginPreloader\.xml",
        // Plugin Preloader (old)
        @"IpHlpAPI\.dll",
    ];

    protected override GamePath? NativePluginFolder => DataPath / "F4SE/Plugins";
}
