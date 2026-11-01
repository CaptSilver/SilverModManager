using DynamicData.Kernel;
using NexusMods.Paths;
using NexusMods.Paths.Utilities;
using SilverModManager.Games.Generic.Simple;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.Games.LoadOrderFile;

/// <summary>
/// Games that read the order their mods load in from a file the app writes on Apply. Steam ids and executables are
/// from each app's Steam launch entry (api.steamcmd.net); a launch entry is a case-insensitive Windows path, so where
/// a game's own entries or the Vortex extension spell a path differently the extension's spelling is used. GOG ids,
/// the mod folder and the file's format are from the Mod Organizer 2 basic_games plugins and the Vortex game
/// extensions (each row says which); Nexus ids from the Nexus games list,
/// https://data.nexusmods.com/file/nexus-data/games.json, by domain.
/// </summary>
public static class LoadOrderFileGames
{
    // A mod is a folder with a mod.manifest in it. Vortex's extension has no layout check; this keeps a stray archive
    // from landing loose in Mods, where the game would read it as no mod at all.
    private static readonly SimpleInstallRules KingdomComeInstall = SimpleInstallPresets.InOwnFolderByMarker("mod.manifest");

    // The second game lists a mod by the id in its manifest (Mod Organizer 2's plugin), which the folder carries.
    private static readonly SimpleInstallRules KingdomComeDeliverance2Install = SimpleInstallPresets.InOwnFolderByMarker("mod.manifest", new ManifestModIdFolderName());

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
        Install = KingdomComeDeliverance2Install,
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

    // Steam's launch entries for the Linux build (XCOM2.sh, XCOM2WotC/XCOM2WotC.sh). Its layout is not read here, so an
    // install holding either is refused. The Windows build holds neither.
    private static readonly RelativePath[] XCom2NativeBuildFiles = ["XCOM2.sh", "XCOM2WotC/XCOM2WotC.sh"];

    // Vortex (game-xcom2) keeps the Workshop mods the options file names.
    private static readonly SteamWorkshopModSource XCom2Workshop = new(268500, new Extension(".XComMod"));

    /// <summary>Vortex (game-xcom2), with the executable as its extension spells it.</summary>
    public static readonly LoadOrderFileGameDefinition XCom2 = new()
    {
        GameId = GameId.From("LoadOrderFile.XCom2"),
        DisplayName = "XCOM 2",
        NexusModsGameId = Optional<NexusModsGameId>.Create(NexusModsGameId.From(1271)),
        SteamAppIds = [268500],
        GogProductIds = [1482002159],
        Executable = "Binaries/Win64/XCom2.exe",
        ModFolder = "XComGame/Mods",
        Install = XCom2Install,
        OrderFile = "XComGame/Config/DefaultModOptions.ini",
        Format = new XComModOptionsIniFormat(),
        ExternalMods = XCom2Workshop,
        NativeBuildFiles = XCom2NativeBuildFiles,
    };

    /// <summary>
    /// Vortex (game-xcom2), with the executable as its extension spells it. A folder holding the DLC's cooked
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
        ExternalMods = XCom2Workshop,
        NativeBuildFiles = XCom2NativeBuildFiles,
    };

    public static IReadOnlyList<LoadOrderFileGameDefinition> All { get; } =
        [KingdomComeDeliverance, KingdomComeDeliverance2, XCom2, XCom2WarOfTheChosen];
}
