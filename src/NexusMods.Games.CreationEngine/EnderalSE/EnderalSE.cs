using DynamicData.Kernel;
using NexusMods.Games.Bethesda.LoadOrder;
using NexusMods.Games.Bethesda.Loot.Masterlists;
using NexusMods.Games.CreationEngine.Abstractions;
using NexusMods.Games.CreationEngine.PluginLists;
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

    // Both Enderals launch Skyrim's executable and ship Skyrim.esm, so only these two tell an Enderal folder apart
    // (gameenderal.cpp getLauncherName and findSteamGame, gameenderalse.cpp's findSteamGame).
    public override IReadOnlyList<RelativePath> IdentifyingFiles => ["Enderal Launcher.exe", "Data/Enderal - Forgotten Stories.esm"];

    protected override RelativePath PrimaryExe => "SkyrimSE.exe";
    public override PluginFormat HeaderFormat => PluginFormat.Tes4;
    public override LootGame LootGame(GameInstallation installation) => Bethesda.Loot.Masterlists.LootGame.EnderalSE;
    public override GamePath PluginsFile => new(LocationId.AppData, "Plugins.txt");
    public override IPluginListMechanism PluginList(GameInstallation installation) => Mechanism;
    private static readonly IPluginListMechanism Mechanism = new AsteriskMechanism();
    public override IReadOnlyList<string> HardcodedPlugins => ["Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm"];
    public override bool SupportsLightPlugins => true;
    public override bool MasterByExtension => true;
    public override GamePath? CreationClubListFile => new(LocationId.Game, "Skyrim.ccc");

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
