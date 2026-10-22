using DynamicData.Kernel;
using NexusMods.Games.Bethesda.LoadOrder;
using NexusMods.Games.Bethesda.Loot.Masterlists;
using NexusMods.Games.CreationEngine.Abstractions;
using NexusMods.Games.CreationEngine.PluginLists;
using NexusMods.Paths;
using NexusMods.Sdk.Games;

namespace NexusMods.Games.CreationEngine.Enderal;

public class Enderal(IServiceProvider provider) : ACreationEngineGame<Enderal>(provider), IGameData<Enderal>
{
    public static GameId GameId { get; } = GameId.From("CreationEngine.Enderal");
    public static string DisplayName => "Enderal: Forgotten Stories";
    public static Optional<Sdk.NexusModsApi.NexusModsGameId> NexusModsGameId => Sdk.NexusModsApi.NexusModsGameId.From(2736);

    public override StoreIdentifiers StoreIdentifiers { get; } = new(GameId)
    {
        // https://github.com/ModOrganizer2/modorganizer-game_enderal/blob/master/src/gameenderal.cpp (steamAPPId)
        SteamAppIds = [933480u],
    };

    // Both Enderals launch Skyrim's executable and ship Skyrim.esm, so only these two tell an Enderal folder apart
    // (gameenderal.cpp getLauncherName and findSteamGame, gameenderalse.cpp's findSteamGame).
    public override IReadOnlyList<RelativePath> IdentifyingFiles => ["Enderal Launcher.exe", "Data/Enderal - Forgotten Stories.esm"];

    protected override RelativePath PrimaryExe => "TESV.exe";
    public override PluginFormat HeaderFormat => PluginFormat.Tes4;
    public override LootGame LootGame(GameInstallation installation) => Bethesda.Loot.Masterlists.LootGame.Enderal;
    public override GamePath PluginsFile => new(LocationId.AppData, "plugins.txt");
    public override IPluginListMechanism PluginList(GameInstallation installation) => Mechanism;
    private static readonly IPluginListMechanism Mechanism = new TextfileMechanism();
    public override IReadOnlyList<string> HardcodedPlugins => ["Skyrim.esm"];

    protected override string LocalAppDataFolderName(GameLocatorResult gameLocatorResult) => "enderal";
    // Both names are from gameenderal.cpp: plugins.txt lives under LocalAppData/enderal (lower case, which
    // matters on ext4), the My Games folder is gameName().
    protected override string MyGamesFolderName(GameLocatorResult gameLocatorResult) => "Enderal";

    protected override string[] GameAliases => ["Enderal", "Enderal Forgotten Stories"];
    protected override string[] StopPatterns => ["(^|/)skse(/|$)"];
    protected override string[] EngineFiles => [@"skse_loader\.exe", @"skse_.*\.dll", @"skse_steam_loader\.dll"];
}
