using DynamicData.Kernel;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.Bethesda.LoadOrder;
using SilverModManager.Games.Bethesda.Loot.Masterlists;
using SilverModManager.Games.CreationEngine.PluginLists;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Games.CreationEngine.Abstractions;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.CreationEngine.Fallout3;

/// <summary>
/// Fallout 3 orders its plugins by file modification time and reads the active ones from plugins.txt in AppData.
/// </summary>
public class Fallout3(IServiceProvider provider) : ACreationEngineGame<Fallout3>(provider), IGameData<Fallout3>, ITimestampOrderedGame
{
    public static GameId GameId { get; } = GameId.From("CreationEngine.Fallout3");
    public static string DisplayName => "Fallout 3";
    public static Optional<Sdk.NexusModsApi.NexusModsGameId> NexusModsGameId => Sdk.NexusModsApi.NexusModsGameId.From(120);

    public override StoreIdentifiers StoreIdentifiers { get; } = new(GameId)
    {
        // 22300 (regular) and 22370 (Game of the Year):
        // https://github.com/ModOrganizer2/modorganizer-game_fallout3/blob/master/src/gamefallout3.cpp (steamAPPId)
        SteamAppIds = [22300u, 22370u],
        // https://www.gogdb.org/product/1454315831 (Game of the Year Edition)
        GOGProductIds = [1454315831L],
    };

    public override PluginFormat HeaderFormat => PluginFormat.Tes4;
    public override LootGame LootGame(GameInstallation installation) => Bethesda.Loot.Masterlists.LootGame.Fallout3;
    public override GamePath PluginsFile => new(LocationId.AppData, "plugins.txt");
    public override IPluginListMechanism PluginList(GameInstallation installation) => Mechanism;
    private static readonly TimestampMechanism Mechanism = new();
    // libloadorder game_settings.rs hardcoded_plugins: none for these games.
    public override IReadOnlyList<string> HardcodedPlugins => [];

    public Task<IReadOnlyList<(GamePath Path, DateTimeOffset Mtime)>> PluginTimestamps(Loadout.ReadOnly loadout, IReadOnlyDictionary<GamePath, SyncNode> syncTree) =>
        PlanPluginTimestamps(loadout, syncTree);

    protected override RelativePath PrimaryExe => "Fallout3.exe";

    // https://github.com/ModOrganizer2/modorganizer-game_fallout3/blob/master/src/gamefallout3.cpp
    // (determineMyGamesPath("Fallout3") and the plugins.txt copy from localAppFolder() + "/Fallout3")
    protected override string LocalAppDataFolderName(GameLocatorResult gameLocatorResult) => "Fallout3";
    protected override string MyGamesFolderName(GameLocatorResult gameLocatorResult) => "Fallout3";

    protected override string[] GameAliases => ["Fallout 3", "Fallout3", "FO3"];
    protected override string[] StopPatterns => ["(^|/)fose(/|$)"];
    protected override string[] EngineFiles => [@"fose_loader\.exe", @"fose_.*\.dll"];
}
