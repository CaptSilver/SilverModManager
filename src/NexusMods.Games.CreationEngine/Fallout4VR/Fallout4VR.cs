using DynamicData.Kernel;
using Mutagen.Bethesda;
using NexusMods.Games.CreationEngine.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk.Games;

namespace NexusMods.Games.CreationEngine.Fallout4VR;

public class Fallout4VR(IServiceProvider provider) : ACreationEngineGame<Fallout4VR>(provider), IGameData<Fallout4VR>
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
    protected override GameRelease PluginRelease => GameRelease.Fallout4VR;
    public override GamePath PluginsFile => new(LocationId.AppData, "plugins.txt");

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
