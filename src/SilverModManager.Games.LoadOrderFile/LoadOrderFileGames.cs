using DynamicData.Kernel;
using SilverModManager.Games.Generic.Simple;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.Games.LoadOrderFile;

/// <summary>
/// Games that read the order their mods load in from a file the app writes on Apply. Steam ids and executables are
/// from each app's Steam launch entry (api.steamcmd.net), so the casing is the depot's; GOG ids, the mod folder and
/// the file's format from the Mod Organizer 2 basic_games plugins and the Vortex game extensions (each row says
/// which); Nexus ids from the Nexus games list, https://data.nexusmods.com/file/nexus-data/games.json, by domain.
/// </summary>
public static class LoadOrderFileGames
{
    // A mod is a folder with a mod.manifest in it. Vortex's extension has no layout check; this keeps a stray archive
    // from landing loose in Mods, where the game would read it as no mod at all.
    private static readonly SimpleInstallRules KingdomComeInstall = SimpleInstallPresets.InOwnFolderByMarker("mod.manifest");

    /// <summary>Vortex (game-kingdomcome-deliverance) and Mod Organizer 2 (game_kingdomcomedeliverance.py).</summary>
    public static readonly LoadOrderFileGameDefinition KingdomComeDeliverance = new()
    {
        GameId = GameId.From("LoadOrderFile.KingdomComeDeliverance"),
        DisplayName = "Kingdom Come: Deliverance",
        NexusModsGameId = Optional<NexusModsGameId>.Create(NexusModsGameId.From(2298)),
        SteamAppIds = [379430],
        GogProductIds = [1719198803],
        Executable = "Bin/Win64/KingdomCome.exe",
        ModFolder = "Mods",
        Install = KingdomComeInstall,
        OrderFile = "Mods/mod_order.txt",
        Format = new ModOrderTxtFormat(),
    };

    /// <summary>Mod Organizer 2 (game_kingdomcomedeliverance2.py).</summary>
    public static readonly LoadOrderFileGameDefinition KingdomComeDeliverance2 = new()
    {
        GameId = GameId.From("LoadOrderFile.KingdomComeDeliverance2"),
        DisplayName = "Kingdom Come: Deliverance II",
        NexusModsGameId = Optional<NexusModsGameId>.Create(NexusModsGameId.From(7286)),
        SteamAppIds = [1771300],
        Executable = "Bin/Win64MasterMasterSteamPGO/KingdomCome.exe",
        ModFolder = "Mods",
        Install = KingdomComeInstall,
        OrderFile = "Mods/mod_order.txt",
        Format = new ModOrderTxtFormat(),
    };

    public static IReadOnlyList<LoadOrderFileGameDefinition> All { get; } = [KingdomComeDeliverance, KingdomComeDeliverance2];
}
