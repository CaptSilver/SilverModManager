using DynamicData.Kernel;
using Mutagen.Bethesda;
using NexusMods.Games.CreationEngine.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk.Games;

namespace NexusMods.Games.CreationEngine.Fallout4;

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
    protected override GameRelease PluginRelease => GameRelease.Fallout4;
    public override GamePath PluginsFile => Fallout4KnownPaths.PluginsFile;

    protected override string LocalAppDataFolderName => "Fallout4";
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
}
