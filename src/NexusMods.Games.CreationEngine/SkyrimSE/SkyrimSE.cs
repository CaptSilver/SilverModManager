using DynamicData.Kernel;
using Mutagen.Bethesda;
using NexusMods.Games.CreationEngine.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk.Games;

namespace NexusMods.Games.CreationEngine.SkyrimSE;

public class SkyrimSE(IServiceProvider provider) : ACreationEngineGame<SkyrimSE>(provider), IGameData<SkyrimSE>
{
    public static GameId GameId { get; } = GameId.From("CreationEngine.SkyrimSE");
    public static string DisplayName => "Skyrim Special Edition";
    public static Optional<Sdk.NexusModsApi.NexusModsGameId> NexusModsGameId => Sdk.NexusModsApi.NexusModsGameId.From(1704);

    public override StoreIdentifiers StoreIdentifiers { get; } = new(GameId)
    {
        SteamAppIds = [489830u],
        GOGProductIds = [1711230643L],
    };

    protected override RelativePath PrimaryExe => "SkyrimSE.exe";
    protected override GameRelease PluginRelease => GameRelease.SkyrimSE;
    public override GamePath PluginsFile => new(LocationId.AppData, "Plugins.txt");

    protected override string LocalAppDataFolderName(GameLocatorResult gameLocatorResult) => "Skyrim Special Edition";
    // The GOG build keeps its My Games folder apart from Steam's.
    protected override string MyGamesFolderName(GameLocatorResult gameLocatorResult)
        => gameLocatorResult.Store == GameStore.GOG ? "Skyrim Special Edition GOG" : "Skyrim Special Edition";

    protected override string[] GameAliases => ["Skyrim Special Edition", "SkyrimSE", "SSE"];
    protected override string[] StopPatterns => ["(^|/)skse(/|$)"];
    protected override string[] EngineFiles => [@"skse64_loader\.exe", @"skse64_.*\.dll"];
}
