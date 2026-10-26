using SilverModManager.Abstractions.Games;
using DynamicData.Kernel;
using SilverModManager.Games.Bethesda.LoadOrder;
using SilverModManager.Games.Bethesda.Loot.Masterlists;
using SilverModManager.Games.CreationEngine.Abstractions;
using SilverModManager.Games.CreationEngine.PluginLists;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.CreationEngine.Fallout4VR;

public class Fallout4VR(IServiceProvider provider) : ACreationEngineGame<Fallout4VR>(provider), IGameData<Fallout4VR>, IHasIniFiles
{
    public static GameId GameId { get; } = GameId.From("CreationEngine.Fallout4VR");
    public static string DisplayName => "Fallout 4 VR";
    // Fallout 4 VR has no Nexus domain of its own: it shares Fallout 4's id, and its mods live there.
    public static Optional<Sdk.NexusModsApi.NexusModsGameId> NexusModsGameId => Sdk.NexusModsApi.NexusModsGameId.From(1151);

    public override StoreIdentifiers StoreIdentifiers { get; } = new(GameId)
    {
        // https://github.com/ModOrganizer2/modorganizer-game_fallout4vr/blob/master/src/gamefallout4vr.cpp (steamAPPId)
        SteamAppIds = [611660u],
    };

    protected override RelativePath PrimaryExe => "Fallout4VR.exe";
    public override PluginFormat HeaderFormat => PluginFormat.Tes4;
    public override LootGame LootGame(GameInstallation installation) => Bethesda.Loot.Masterlists.LootGame.Fallout4VR;
    public override GamePath PluginsFile => new(LocationId.AppData, "plugins.txt");
    public override IPluginListMechanism PluginList(GameInstallation installation) => Mechanism;
    private static readonly IPluginListMechanism Mechanism = new AsteriskMechanism();
    public override IReadOnlyList<string> HardcodedPlugins => ["Fallout4.esm", "Fallout4_VR.esm"];
    public override bool SupportsLightPlugins => true;
    public override bool MasterByExtension => true;
    public override IReadOnlyList<GamePath> TestFileIniFiles => [new(LocationId.Preferences, "Fallout4VR.ini"), new(LocationId.Preferences, "Fallout4VRCustom.ini")];

    // https://github.com/ModOrganizer2/modorganizer-game_fallout4vr/blob/master/src/gamefallout4vr.cpp
    // (determineMyGamesPath and the plugins.txt copy from localAppFolder both use "Fallout4VR")
    protected override string LocalAppDataFolderName(GameLocatorResult gameLocatorResult) => "Fallout4VR";
    protected override string MyGamesFolderName(GameLocatorResult gameLocatorResult) => "Fallout4VR";

    protected override string[] GameAliases => ["Fallout 4 VR", "Fallout4VR", "FO4VR"];
    protected override string[] StopPatterns => ["(^|/)f4se(/|$)"];
    protected override string[] EngineFiles =>
    [
        // F4SEVR
        @"f4sevr_loader\.exe",
        @"f4sevr_.*\.dll",
        // Plugin Preloader (new)
        @"winhttp\.dll",
        @"xSE\ PluginPreloader\.xml",
        // Plugin Preloader (old)
        @"IpHlpAPI\.dll",
    ];
}
