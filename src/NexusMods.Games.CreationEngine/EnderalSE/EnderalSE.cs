using DynamicData.Kernel;
using Mutagen.Bethesda;
using NexusMods.Games.CreationEngine.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk.Games;

namespace NexusMods.Games.CreationEngine.EnderalSE;

public class EnderalSE(IServiceProvider provider) : ACreationEngineGame<EnderalSE>(provider), IGameData<EnderalSE>
{
    public static GameId GameId { get; } = GameId.From("CreationEngine.EnderalSE");
    public static string DisplayName => "Enderal: Forgotten Stories (Special Edition)";
    public static Optional<Sdk.NexusModsApi.NexusModsGameId> NexusModsGameId => Sdk.NexusModsApi.NexusModsGameId.From(3685);

    public override StoreIdentifiers StoreIdentifiers { get; } = new(GameId)
    {
        // https://github.com/ModOrganizer2/modorganizer-game_enderalse/blob/master/src/gameenderalse.cpp (steamAPPId)
        SteamAppIds = [976620u],
        // https://www.gogdb.org/product/1708684988 (titled "Enderal: Forgotten Stories"; GOG only sells the
        // SE build), also the GOG registry key in gameenderalse.cpp.
        GOGProductIds = [1708684988L],
    };

    protected override RelativePath PrimaryExe => "SkyrimSE.exe";
    protected override GameRelease PluginRelease => GameRelease.EnderalSE;
    public override GamePath PluginsFile => new(LocationId.AppData, "Plugins.txt");

    // The GOG build keeps both its plugin list and its My Games folder apart from Steam's: gameenderalse.cpp
    // builds both paths from gameDirectoryName().
    protected override string LocalAppDataFolderName(GameLocatorResult gameLocatorResult) => DirectoryName(gameLocatorResult);
    protected override string MyGamesFolderName(GameLocatorResult gameLocatorResult) => DirectoryName(gameLocatorResult);

    private static string DirectoryName(GameLocatorResult gameLocatorResult)
        => gameLocatorResult.Store == GameStore.GOG ? "Enderal Special Edition GOG" : "Enderal Special Edition";

    protected override string[] GameAliases => ["Enderal Special Edition", "EnderalSE", "Enderal SE"];
    protected override string[] StopPatterns => ["(^|/)skse(/|$)"];
    protected override string[] EngineFiles => [@"skse64_loader\.exe", @"skse64_.*\.dll"];
}
