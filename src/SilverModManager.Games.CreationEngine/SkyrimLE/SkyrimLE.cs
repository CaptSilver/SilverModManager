using DynamicData.Kernel;
using SilverModManager.Games.Bethesda.LoadOrder;
using SilverModManager.Games.Bethesda.Loot.Masterlists;
using SilverModManager.Games.CreationEngine.Abstractions;
using SilverModManager.Games.CreationEngine.PluginLists;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.CreationEngine.SkyrimLE;

public class SkyrimLE(IServiceProvider provider) : ACreationEngineGame<SkyrimLE>(provider), IGameData<SkyrimLE>
{
    public static GameId GameId { get; } = GameId.From("CreationEngine.SkyrimLE");
    public static string DisplayName => "Skyrim";
    public static Optional<Sdk.NexusModsApi.NexusModsGameId> NexusModsGameId => Sdk.NexusModsApi.NexusModsGameId.From(110);

    public override StoreIdentifiers StoreIdentifiers { get; } = new(GameId)
    {
        // https://github.com/ModOrganizer2/modorganizer-game_skyrim/blob/master/src/gameskyrim.cpp (steamAPPId)
        SteamAppIds = [72850u],
    };

    protected override RelativePath PrimaryExe => "TESV.exe";
    public override PluginFormat HeaderFormat => PluginFormat.Tes4;
    public override LootGame LootGame(GameInstallation installation) => Bethesda.Loot.Masterlists.LootGame.Skyrim;
    public override GamePath PluginsFile => new(LocationId.AppData, "plugins.txt");
    public override IPluginListMechanism PluginList(GameInstallation installation) => Mechanism;
    private static readonly IPluginListMechanism Mechanism = new TextfileMechanism();
    // libloadorder game_settings.rs implicitly_active_plugins: Update.esm is always active. Listing it as an early
    // loader keeps it on and puts it right after Skyrim.esm, where listing it in plugins.txt would put it too.
    public override IReadOnlyList<string> HardcodedPlugins => ["Skyrim.esm", "Update.esm"];

    protected override string LocalAppDataFolderName(GameLocatorResult gameLocatorResult) => "Skyrim";
    // gameName() is the My Games folder in modorganizer-game_skyrim; no store variant changes it.
    protected override string MyGamesFolderName(GameLocatorResult gameLocatorResult) => "Skyrim";

    protected override string[] GameAliases => ["Skyrim", "TESV", "Skyrim Legendary Edition"];
    protected override string[] StopPatterns => ["(^|/)skse(/|$)"];
    protected override string[] EngineFiles => [@"skse_loader\.exe", @"skse_.*\.dll", @"skse_steam_loader\.dll"];
}
