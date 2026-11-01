using DynamicData.Kernel;
using NexusMods.Paths;
using NexusMods.Paths.Utilities;
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

    // The .XComMod file names the mod and must match its folder, so a mod goes in a folder named after it (Vortex's
    // installer does the same). The game and Vortex's extension tell the two games apart by the folder alone.
    private static readonly SimpleInstallRules XCom2Install = new()
    {
        DescriptorRules =
        [
            new SimpleDescriptorRule
            {
                Extension = new Extension(".XComMod"),
                Destination = RelativePath.Empty,
                IgnoredExtensions = [KnownExtensions.Txt, KnownExtensions.Md, KnownExtensions.Png, KnownExtensions.Jpg],
            },
        ],
    };

    // Steam's 268500 and the GOG release hold both games: War of the Chosen is a folder inside the base game's.
    // Vortex's extension (game-xcom2) gives both games the same store ids.
    private static readonly RelativePath XCom2WarOfTheChosenFolder = "XCom2-WarOfTheChosen";

    /// <summary>Vortex (game-xcom2); the executable is Steam's launch entry for the base game.</summary>
    public static readonly LoadOrderFileGameDefinition XCom2 = new()
    {
        GameId = GameId.From("LoadOrderFile.XCom2"),
        DisplayName = "XCOM 2",
        NexusModsGameId = Optional<NexusModsGameId>.Create(NexusModsGameId.From(1271)),
        SteamAppIds = [268500],
        GogProductIds = [1482002159],
        Executable = "binaries/Win64/XCom2.exe",
        ModFolder = "XComGame/Mods",
        Install = XCom2Install,
        OrderFile = "XComGame/Config/DefaultModOptions.ini",
        Format = new XComModOptionsIniFormat(),
    };

    /// <summary>
    /// Vortex (game-xcom2); the executable is Steam's launch entry for the DLC. A folder holding the DLC's cooked
    /// files is this game, which is how the locator picks it over the base game for the shared store ids.
    /// </summary>
    public static readonly LoadOrderFileGameDefinition XCom2WarOfTheChosen = new()
    {
        GameId = GameId.From("LoadOrderFile.XCom2WarOfTheChosen"),
        DisplayName = "XCOM 2: War of the Chosen",
        NexusModsGameId = Optional<NexusModsGameId>.Create(NexusModsGameId.From(1271)),
        SteamAppIds = [268500],
        GogProductIds = [1482002159],
        Executable = XCom2WarOfTheChosenFolder / "Binaries/Win64/XCom2.exe",
        ModFolder = XCom2WarOfTheChosenFolder / "XComGame/Mods",
        Install = XCom2Install,
        OrderFile = XCom2WarOfTheChosenFolder / "XComGame/Config/DefaultModOptions.ini",
        Format = new XComModOptionsIniFormat(),
        IdentifyingFiles = [XCom2WarOfTheChosenFolder / "XComGame/CookedPCConsole/3DUIBP.upk"],
    };

    public static IReadOnlyList<LoadOrderFileGameDefinition> All { get; } =
        [KingdomComeDeliverance, KingdomComeDeliverance2, XCom2, XCom2WarOfTheChosen];
}
