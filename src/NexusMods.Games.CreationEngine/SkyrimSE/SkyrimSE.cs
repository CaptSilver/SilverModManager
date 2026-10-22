using DynamicData.Kernel;
using NexusMods.Games.Bethesda.LoadOrder;
using NexusMods.Games.CreationEngine.Abstractions;
using NexusMods.Games.CreationEngine.PluginLists;
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
    public override PluginFormat HeaderFormat => PluginFormat.Tes4;
    public override GamePath PluginsFile => new(LocationId.AppData, "Plugins.txt");
    public override IPluginListMechanism PluginList(GameInstallation installation) => Mechanism;
    private static readonly IPluginListMechanism Mechanism = new AsteriskMechanism();
    public override IReadOnlyList<string> HardcodedPlugins => ["Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm"];
    public override bool SupportsLightPlugins => true;
    public override bool MasterByExtension => true;
    public override GamePath? CreationClubListFile => new(LocationId.Game, "Skyrim.ccc");

    // The GOG build keeps both its plugin list and its My Games folder apart from Steam's: gameskyrimse.cpp
    // builds both paths from gameDirectoryName(), which adds the " GOG" suffix for the GOG variant.
    protected override string LocalAppDataFolderName(GameLocatorResult gameLocatorResult) => DirectoryName(gameLocatorResult);
    protected override string MyGamesFolderName(GameLocatorResult gameLocatorResult) => DirectoryName(gameLocatorResult);

    private static string DirectoryName(GameLocatorResult gameLocatorResult)
        => gameLocatorResult.Store == GameStore.GOG ? "Skyrim Special Edition GOG" : "Skyrim Special Edition";

    protected override string[] GameAliases => ["Skyrim Special Edition", "SkyrimSE", "SSE"];
    protected override string[] StopPatterns => ["(^|/)skse(/|$)"];
    protected override string[] EngineFiles => [@"skse64_loader\.exe", @"skse64_.*\.dll"];
}
