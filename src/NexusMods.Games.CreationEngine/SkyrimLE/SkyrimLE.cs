using DynamicData.Kernel;
using NexusMods.Games.Bethesda.LoadOrder;
using NexusMods.Games.CreationEngine.Abstractions;
using NexusMods.Games.CreationEngine.PluginLists;
using NexusMods.Paths;
using NexusMods.Sdk.Games;

namespace NexusMods.Games.CreationEngine.SkyrimLE;

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
    public override GamePath PluginsFile => new(LocationId.AppData, "plugins.txt");
    public override IPluginListMechanism PluginList(GameInstallation installation) => Mechanism;
    private static readonly IPluginListMechanism Mechanism = new PlainMechanism();
    public override IReadOnlyList<string> HardcodedPlugins => ["Skyrim.esm"];

    protected override string LocalAppDataFolderName(GameLocatorResult gameLocatorResult) => "Skyrim";
    // gameName() is the My Games folder in modorganizer-game_skyrim; no store variant changes it.
    protected override string MyGamesFolderName(GameLocatorResult gameLocatorResult) => "Skyrim";

    protected override string[] GameAliases => ["Skyrim", "TESV", "Skyrim Legendary Edition"];
    protected override string[] StopPatterns => ["(^|/)skse(/|$)"];
    protected override string[] EngineFiles => [@"skse_loader\.exe", @"skse_.*\.dll", @"skse_steam_loader\.dll"];
}
