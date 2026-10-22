using DynamicData.Kernel;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Games.Bethesda.LoadOrder;
using NexusMods.Games.Bethesda.Loot.Masterlists;
using NexusMods.Games.CreationEngine.PluginLists;
using NexusMods.Sdk.Loadouts;
using NexusMods.Games.CreationEngine.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk.Games;

namespace NexusMods.Games.CreationEngine.FalloutNewVegas;

/// <summary>
/// New Vegas orders its plugins by file modification time and reads the active ones from plugins.txt in AppData.
/// Tale of Two Wastelands is a mod with its own installer, not a separate game.
/// </summary>
public class FalloutNewVegas(IServiceProvider provider) : ACreationEngineGame<FalloutNewVegas>(provider), IGameData<FalloutNewVegas>, ITimestampOrderedGame
{
    public static GameId GameId { get; } = GameId.From("CreationEngine.FalloutNewVegas");
    public static string DisplayName => "Fallout: New Vegas";
    // The Nexus domain is "newvegas".
    public static Optional<Sdk.NexusModsApi.NexusModsGameId> NexusModsGameId => Sdk.NexusModsApi.NexusModsGameId.From(130);

    public override StoreIdentifiers StoreIdentifiers { get; } = new(GameId)
    {
        // 22380, and 22490 for the English/Polish/Czech/Russian release (installed under a folder ending in "enplczru"):
        // https://github.com/ModOrganizer2/modorganizer-game_falloutnv/blob/master/src/gamefalloutnv.cpp (steamAPPId)
        SteamAppIds = [22380u, 22490u],
        // https://www.gogdb.org/product/1454587428 (Ultimate Edition)
        GOGProductIds = [1454587428L],
    };

    public override PluginFormat HeaderFormat => PluginFormat.Tes4;
    public override LootGame LootGame(GameInstallation installation) => Bethesda.Loot.Masterlists.LootGame.FalloutNV;
    public override GamePath PluginsFile => new(LocationId.AppData, "plugins.txt");
    public override IPluginListMechanism PluginList(GameInstallation installation) => Mechanism;
    private static readonly TimestampMechanism Mechanism = new();
    // libloadorder game_settings.rs hardcoded_plugins: none for these games.
    public override IReadOnlyList<string> HardcodedPlugins => [];

    // libloadorder game_settings.rs (find_nam_plugins): a .nam file in Data activates the .esp and .esm of the same name.
    public override IReadOnlyList<string> ImplicitlyActivePlugins(IEnumerable<string> dataFileNames) => dataFileNames
        .Where(name => name.EndsWith(".nam", StringComparison.OrdinalIgnoreCase))
        .SelectMany(name => new[] { System.IO.Path.ChangeExtension(name, ".esp"), System.IO.Path.ChangeExtension(name, ".esm") })
        .ToList();

    public Task<IReadOnlyList<(GamePath Path, DateTimeOffset Mtime)>> PluginTimestamps(Loadout.ReadOnly loadout, IReadOnlyDictionary<GamePath, SyncNode> syncTree) =>
        PlanPluginTimestamps(loadout, syncTree);

    protected override RelativePath PrimaryExe => "FalloutNV.exe";

    // https://github.com/ModOrganizer2/modorganizer-game_falloutnv/blob/master/src/gamefalloutnv.cpp
    // (gameDirectoryName() is "FalloutNV" outside the Epic release; it names both the My Games folder
    // and the plugins.txt copy from localAppFolder())
    protected override string LocalAppDataFolderName(GameLocatorResult gameLocatorResult) => "FalloutNV";
    protected override string MyGamesFolderName(GameLocatorResult gameLocatorResult) => "FalloutNV";

    protected override string[] GameAliases => ["Fallout New Vegas", "Fallout: New Vegas", "FalloutNV", "FNV"];
    protected override string[] StopPatterns => ["(^|/)nvse(/|$)"];
    protected override string[] EngineFiles => [@"nvse_loader\.exe", @"nvse_.*\.dll", @"nvse_steam_loader\.dll"];
}
