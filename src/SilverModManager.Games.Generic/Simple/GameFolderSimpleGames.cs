using DynamicData.Kernel;
using NexusMods.Paths;
using NexusMods.Paths.Utilities;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.Games.Generic.Simple;

/// <summary>
/// Simple games whose mods go in a folder inside the game folder. Executables, store ids and mod folders are read
/// from the Mod Organizer 2 basic_games plugins and the Vortex game extensions (each row says which); Nexus ids come
/// from the Nexus games list, https://data.nexusmods.com/file/nexus-data/games.json, by the domain the plugin or
/// extension names. Epic ids are left out, and so are native Linux builds, until each is checked.
/// </summary>
public static class GameFolderSimpleGames
{
    // Dungeon Siege reads loose .dsres/.dsmap files and the Resources and Maps folders; the sequel uses .ds2res/.ds2map.
    private static SimpleInstallRules ResourcesAndMaps(string resourceExtension, string mapExtension) => new()
    {
        Targets =
        [
            new SimpleInstallTarget
            {
                Destination = "Resources",
                KnownSourceFolderNames = ["Resources"],
                KnownValidFileExtensions = [new Extension(resourceExtension)],
            },
            new SimpleInstallTarget
            {
                Destination = "Maps",
                KnownSourceFolderNames = ["Maps"],
                KnownValidFileExtensions = [new Extension(mapExtension)],
            },
        ],
    };

    // The plugin or extension publishes no layout to match, so the archive goes in as it is laid out.
    private static readonly SimpleInstallRules AsIs = new() { InstallAsIs = true };

    /// <summary>From Mod Organizer 2.</summary>
    public static readonly SimpleGameDefinition DarkestDungeon = new()
    {
        GameId = GameId.From("Simple.DarkestDungeon"),
        DisplayName = "Darkest Dungeon",
        NexusModsGameId = NexusModsGameId.From(804),
        Stores = new StoreIdentifiers(GameId.From("Simple.DarkestDungeon")) { SteamAppIds = [262060], GOGProductIds = [1450711444] },
        Executable = "_windows/win64/Darkest.exe",
        ModRoot = new ModLocation(ModLocationRoot.GameFolder, RelativePath.Empty),
        Install = AsIs,
    };

    /// <summary>From Mod Organizer 2.</summary>
    public static readonly SimpleGameDefinition DungeonSiege = new()
    {
        GameId = GameId.From("Simple.DungeonSiege"),
        DisplayName = "Dungeon Siege",
        NexusModsGameId = NexusModsGameId.From(541),
        Stores = new StoreIdentifiers(GameId.From("Simple.DungeonSiege")) { SteamAppIds = [39190] },
        Executable = "DungeonSiege.exe",
        ModRoot = new ModLocation(ModLocationRoot.GameFolder, RelativePath.Empty),
        Install = ResourcesAndMaps(".dsres", ".dsmap"),
    };

    /// <summary>From Mod Organizer 2.</summary>
    public static readonly SimpleGameDefinition DungeonSiege2 = new()
    {
        GameId = GameId.From("Simple.DungeonSiege2"),
        DisplayName = "Dungeon Siege II",
        NexusModsGameId = NexusModsGameId.From(2078),
        Stores = new StoreIdentifiers(GameId.From("Simple.DungeonSiege2")) { SteamAppIds = [39200] },
        Executable = "DungeonSiege2.exe",
        ModRoot = new ModLocation(ModLocationRoot.GameFolder, RelativePath.Empty),
        Install = ResourcesAndMaps(".ds2res", ".ds2map"),
    };

    /// <summary>From Mod Organizer 2.</summary>
    public static readonly SimpleGameDefinition DarkSouls = new()
    {
        GameId = GameId.From("Simple.DarkSouls"),
        DisplayName = "Dark Souls",
        NexusModsGameId = NexusModsGameId.From(162),
        Stores = new StoreIdentifiers(GameId.From("Simple.DarkSouls")) { SteamAppIds = [211420] },
        Executable = "DATA/DARKSOULS.exe",
        ModRoot = new ModLocation(ModLocationRoot.GameFolder, "DATA"),
        Install = AsIs,
    };

    /// <summary>From Mod Organizer 2.</summary>
    public static readonly SimpleGameDefinition DarkSoulsRemastered = new()
    {
        GameId = GameId.From("Simple.DarkSoulsRemastered"),
        DisplayName = "Dark Souls Remastered",
        NexusModsGameId = NexusModsGameId.From(2432),
        Stores = new StoreIdentifiers(GameId.From("Simple.DarkSoulsRemastered")) { SteamAppIds = [570940] },
        Executable = "DarkSoulsRemastered.exe",
        ModRoot = new ModLocation(ModLocationRoot.GameFolder, RelativePath.Empty),
        Install = AsIs,
    };

    /// <summary>From Mod Organizer 2.</summary>
    public static readonly SimpleGameDefinition DarkSouls2Sotfs = new()
    {
        GameId = GameId.From("Simple.DarkSouls2Sotfs"),
        DisplayName = "Dark Souls II: Scholar of the First Sin",
        NexusModsGameId = NexusModsGameId.From(482),
        Stores = new StoreIdentifiers(GameId.From("Simple.DarkSouls2Sotfs")) { SteamAppIds = [335300] },
        Executable = "Game/DarkSoulsII.exe",
        ModRoot = new ModLocation(ModLocationRoot.GameFolder, "Game"),
        Install = AsIs,
    };

    /// <summary>From Mod Organizer 2.</summary>
    public static readonly SimpleGameDefinition DragonsDogmaDarkArisen = new()
    {
        GameId = GameId.From("Simple.DragonsDogmaDarkArisen"),
        DisplayName = "Dragon's Dogma: Dark Arisen",
        NexusModsGameId = NexusModsGameId.From(1249),
        Stores = new StoreIdentifiers(GameId.From("Simple.DragonsDogmaDarkArisen")) { SteamAppIds = [367500], GOGProductIds = [1242384383] },
        Executable = "DDDA.exe",
        ModRoot = new ModLocation(ModLocationRoot.GameFolder, "nativePC"),
        Install = AsIs,
    };

    /// <summary>From Mod Organizer 2.</summary>
    public static readonly SimpleGameDefinition DarkMessiah = new()
    {
        GameId = GameId.From("Simple.DarkMessiah"),
        DisplayName = "Dark Messiah of Might & Magic",
        NexusModsGameId = NexusModsGameId.From(628),
        Stores = new StoreIdentifiers(GameId.From("Simple.DarkMessiah")) { SteamAppIds = [2100] },
        Executable = "mm.exe",
        ModRoot = new ModLocation(ModLocationRoot.GameFolder, "mm"),
        Install = AsIs,
    };

    /// <summary>From Mod Organizer 2.</summary>
    public static readonly SimpleGameDefinition X4Foundations = new()
    {
        GameId = GameId.From("Simple.X4Foundations"),
        DisplayName = "X4: Foundations",
        NexusModsGameId = NexusModsGameId.From(2659),
        Stores = new StoreIdentifiers(GameId.From("Simple.X4Foundations")) { SteamAppIds = [392160] },
        Executable = "x4.exe",
        ModRoot = new ModLocation(ModLocationRoot.GameFolder, "extensions"),
        Install = AsIs,
    };

    /// <summary>From Mod Organizer 2.</summary>
    public static readonly SimpleGameDefinition KerbalSpaceProgram = new()
    {
        GameId = GameId.From("Simple.KerbalSpaceProgram"),
        DisplayName = "Kerbal Space Program",
        NexusModsGameId = NexusModsGameId.From(272),
        Stores = new StoreIdentifiers(GameId.From("Simple.KerbalSpaceProgram")) { SteamAppIds = [220200, 283740, 982970] },
        Executable = "KSP_x64.exe",
        ModRoot = new ModLocation(ModLocationRoot.GameFolder, "GameData"),
        Install = AsIs,
    };

    /// <summary>From Mod Organizer 2.</summary>
    public static readonly SimpleGameDefinition VampireBloodlines = new()
    {
        GameId = GameId.From("Simple.VampireBloodlines"),
        DisplayName = "Vampire: The Masquerade - Bloodlines",
        NexusModsGameId = NexusModsGameId.From(437),
        Stores = new StoreIdentifiers(GameId.From("Simple.VampireBloodlines")) { SteamAppIds = [2600], GOGProductIds = [1207659240] },
        Executable = "vampire.exe",
        ModRoot = new ModLocation(ModLocationRoot.GameFolder, "vampire"),
        Install = AsIs,
    };

    /// <summary>From Mod Organizer 2.</summary>
    public static readonly SimpleGameDefinition Control = new()
    {
        GameId = GameId.From("Simple.Control"),
        DisplayName = "Control",
        NexusModsGameId = NexusModsGameId.From(2936),
        Stores = new StoreIdentifiers(GameId.From("Simple.Control")) { SteamAppIds = [870780], GOGProductIds = [2049187585] },
        Executable = "Control.exe",
        ModRoot = new ModLocation(ModLocationRoot.GameFolder, RelativePath.Empty),
        Install = AsIs,
    };

    /// <summary>From Mod Organizer 2.</summary>
    public static readonly SimpleGameDefinition BatmanArkhamCity = new()
    {
        GameId = GameId.From("Simple.BatmanArkhamCity"),
        DisplayName = "Batman: Arkham City",
        NexusModsGameId = NexusModsGameId.From(372),
        Stores = new StoreIdentifiers(GameId.From("Simple.BatmanArkhamCity")) { SteamAppIds = [200260], GOGProductIds = [1260066469] },
        Executable = "Binaries/Win32/BatmanAC.exe",
        ModRoot = new ModLocation(ModLocationRoot.GameFolder, "BmGame"),
        Install = AsIs,
    };

    /// <summary>From Mod Organizer 2.</summary>
    public static readonly SimpleGameDefinition AssettoCorsa = new()
    {
        GameId = GameId.From("Simple.AssettoCorsa"),
        DisplayName = "Assetto Corsa",
        NexusModsGameId = NexusModsGameId.From(556),
        Stores = new StoreIdentifiers(GameId.From("Simple.AssettoCorsa")) { SteamAppIds = [244210] },
        Executable = "AssettoCorsa.exe",
        ModRoot = new ModLocation(ModLocationRoot.GameFolder, RelativePath.Empty),
        Install = AsIs,
    };

    /// <summary>From Mod Organizer 2.</summary>
    public static readonly SimpleGameDefinition Borderlands = new()
    {
        GameId = GameId.From("Simple.Borderlands"),
        DisplayName = "Borderlands",
        NexusModsGameId = Optional<NexusModsGameId>.None,
        Stores = new StoreIdentifiers(GameId.From("Simple.Borderlands")) { SteamAppIds = [8980] },
        Executable = "Binaries/Borderlands.exe",
        ModRoot = new ModLocation(ModLocationRoot.GameFolder, RelativePath.Empty),
        Install = AsIs,
    };

    /// <summary>From Mod Organizer 2.</summary>
    public static readonly SimpleGameDefinition F123 = new()
    {
        GameId = GameId.From("Simple.F123"),
        DisplayName = "F1 23",
        NexusModsGameId = NexusModsGameId.From(5513),
        Stores = new StoreIdentifiers(GameId.From("Simple.F123")) { SteamAppIds = [2108330] },
        Executable = "F1_23.exe",
        ModRoot = new ModLocation(ModLocationRoot.GameFolder, RelativePath.Empty),
        Install = AsIs,
    };

    /// <summary>From Mod Organizer 2.</summary>
    public static readonly SimpleGameDefinition KarrynsPrison = new()
    {
        GameId = GameId.From("Simple.KarrynsPrison"),
        DisplayName = "Karryn's Prison",
        NexusModsGameId = Optional<NexusModsGameId>.None,
        Stores = new StoreIdentifiers(GameId.From("Simple.KarrynsPrison")) { SteamAppIds = [1619750] },
        Executable = "nw.exe",
        ModRoot = new ModLocation(ModLocationRoot.GameFolder, RelativePath.Empty),
        Install = AsIs,
    };

    /// <summary>From Mod Organizer 2.</summary>
    public static readonly SimpleGameDefinition MetalGearSolid2MasterCollection = new()
    {
        GameId = GameId.From("Simple.MetalGearSolid2MasterCollection"),
        DisplayName = "Metal Gear Solid 2: Sons of Liberty - Master Collection",
        NexusModsGameId = NexusModsGameId.From(5954),
        Stores = new StoreIdentifiers(GameId.From("Simple.MetalGearSolid2MasterCollection")) { SteamAppIds = [2131640] },
        Executable = "METAL GEAR SOLID2.exe",
        ModRoot = new ModLocation(ModLocationRoot.GameFolder, RelativePath.Empty),
        Install = AsIs,
    };

    /// <summary>From Mod Organizer 2.</summary>
    public static readonly SimpleGameDefinition MetalGearSolid3MasterCollection = new()
    {
        GameId = GameId.From("Simple.MetalGearSolid3MasterCollection"),
        DisplayName = "Metal Gear Solid 3: Snake Eater - Master Collection",
        NexusModsGameId = NexusModsGameId.From(5946),
        Stores = new StoreIdentifiers(GameId.From("Simple.MetalGearSolid3MasterCollection")) { SteamAppIds = [2131650] },
        Executable = "METAL GEAR SOLID3.exe",
        ModRoot = new ModLocation(ModLocationRoot.GameFolder, RelativePath.Empty),
        Install = AsIs,
    };

    /// <summary>From Mod Organizer 2.</summary>
    public static readonly SimpleGameDefinition MirrorsEdge = new()
    {
        GameId = GameId.From("Simple.MirrorsEdge"),
        DisplayName = "Mirror's Edge",
        NexusModsGameId = NexusModsGameId.From(5614),
        Stores = new StoreIdentifiers(GameId.From("Simple.MirrorsEdge")) { SteamAppIds = [17410], GOGProductIds = [1893001152] },
        Executable = "Binaries/MirrorsEdge.exe",
        ModRoot = new ModLocation(ModLocationRoot.GameFolder, "TdGame"),
        Install = AsIs,
    };

    /// <summary>From Mod Organizer 2.</summary>
    public static readonly SimpleGameDefinition NierAutomata = new()
    {
        GameId = GameId.From("Simple.NierAutomata"),
        DisplayName = "NieR: Automata",
        NexusModsGameId = NexusModsGameId.From(1950),
        Stores = new StoreIdentifiers(GameId.From("Simple.NierAutomata")) { SteamAppIds = [524220] },
        Executable = "NieRAutomata.exe",
        ModRoot = new ModLocation(ModLocationRoot.GameFolder, RelativePath.Empty),
        Install = AsIs,
    };

    /// <summary>From Mod Organizer 2.</summary>
    public static readonly SimpleGameDefinition TestDriveUnlimited2 = new()
    {
        GameId = GameId.From("Simple.TestDriveUnlimited2"),
        DisplayName = "Test Drive Unlimited 2",
        NexusModsGameId = NexusModsGameId.From(2353),
        Stores = new StoreIdentifiers(GameId.From("Simple.TestDriveUnlimited2")) { SteamAppIds = [9930] },
        Executable = "UpLauncher.exe",
        ModRoot = new ModLocation(ModLocationRoot.GameFolder, RelativePath.Empty),
        Install = AsIs,
    };

    /// <summary>From Mod Organizer 2.</summary>
    public static readonly SimpleGameDefinition TrackmaniaUnitedForever = new()
    {
        GameId = GameId.From("Simple.TrackmaniaUnitedForever"),
        DisplayName = "Trackmania United Forever",
        NexusModsGameId = NexusModsGameId.From(1500),
        Stores = new StoreIdentifiers(GameId.From("Simple.TrackmaniaUnitedForever")) { SteamAppIds = [7200] },
        Executable = "TmForeverLauncher.exe",
        ModRoot = new ModLocation(ModLocationRoot.GameFolder, "GameData"),
        Install = AsIs,
    };

    /// <summary>From Mod Organizer 2.</summary>
    public static readonly SimpleGameDefinition TrainSimulator = new()
    {
        GameId = GameId.From("Simple.TrainSimulator"),
        DisplayName = "Train Simulator",
        NexusModsGameId = Optional<NexusModsGameId>.None,
        Stores = new StoreIdentifiers(GameId.From("Simple.TrainSimulator")) { SteamAppIds = [24010] },
        Executable = "RailWorks.exe",
        ModRoot = new ModLocation(ModLocationRoot.GameFolder, RelativePath.Empty),
        Install = AsIs,
    };

    /// <summary>From Mod Organizer 2.</summary>
    public static readonly SimpleGameDefinition ValkyriaChronicles = new()
    {
        GameId = GameId.From("Simple.ValkyriaChronicles"),
        DisplayName = "Valkyria Chronicles",
        NexusModsGameId = NexusModsGameId.From(5522),
        Stores = new StoreIdentifiers(GameId.From("Simple.ValkyriaChronicles")) { SteamAppIds = [294860] },
        Executable = "Valkyria.exe",
        ModRoot = new ModLocation(ModLocationRoot.GameFolder, RelativePath.Empty),
        Install = AsIs,
    };

    /// <summary>From Mod Organizer 2.</summary>
    public static readonly SimpleGameDefinition ZeusAndPoseidon = new()
    {
        GameId = GameId.From("Simple.ZeusAndPoseidon"),
        DisplayName = "Zeus and Poseidon",
        NexusModsGameId = Optional<NexusModsGameId>.None,
        Stores = new StoreIdentifiers(GameId.From("Simple.ZeusAndPoseidon")) { SteamAppIds = [566050], GOGProductIds = [1207659039] },
        Executable = "Zeus.exe",
        ModRoot = new ModLocation(ModLocationRoot.GameFolder, "Adventures"),
        Install = AsIs,
    };

    /// <summary>From Vortex.</summary>
    public static readonly SimpleGameDefinition AHatInTime = new()
    {
        GameId = GameId.From("Simple.AHatInTime"),
        DisplayName = "A Hat in Time",
        NexusModsGameId = NexusModsGameId.From(2361),
        Stores = new StoreIdentifiers(GameId.From("Simple.AHatInTime")) { SteamAppIds = [253230] },
        Executable = "Binaries/Win64/HatInTimeGame.exe",
        ModRoot = new ModLocation(ModLocationRoot.GameFolder, "HatInTimeGame/Mods"),
        Install = AsIs,
    };

    /// <summary>From Vortex.</summary>
    public static readonly SimpleGameDefinition BreakingWheel = new()
    {
        GameId = GameId.From("Simple.BreakingWheel"),
        DisplayName = "Breaking Wheel",
        NexusModsGameId = NexusModsGameId.From(1767),
        Stores = new StoreIdentifiers(GameId.From("Simple.BreakingWheel")) { SteamAppIds = [545890] },
        Executable = "Ellie_Ball_Project.exe",
        ModRoot = new ModLocation(ModLocationRoot.GameFolder, "ModdingTools"),
        Install = AsIs,
    };

    /// <summary>From Vortex.</summary>
    public static readonly SimpleGameDefinition DawnOfMan = new()
    {
        GameId = GameId.From("Simple.DawnOfMan"),
        DisplayName = "Dawn of Man",
        NexusModsGameId = NexusModsGameId.From(2748),
        Stores = new StoreIdentifiers(GameId.From("Simple.DawnOfMan")) { SteamAppIds = [858810], GOGProductIds = [1899257943] },
        Executable = "DawnOfMan.exe",
        ModRoot = new ModLocation(ModLocationRoot.GameFolder, "Mods"),
        Install = AsIs,
    };

    /// <summary>From Vortex.</summary>
    public static readonly SimpleGameDefinition Elex = new()
    {
        GameId = GameId.From("Simple.Elex"),
        DisplayName = "Elex",
        NexusModsGameId = NexusModsGameId.From(2144),
        Stores = new StoreIdentifiers(GameId.From("Simple.Elex")) { SteamAppIds = [411300] },
        Executable = "system/ELEX.exe",
        ModRoot = new ModLocation(ModLocationRoot.GameFolder, "data/packed"),
        Install = AsIs,
    };

    /// <summary>From Vortex.</summary>
    public static readonly SimpleGameDefinition GreedFall = new()
    {
        GameId = GameId.From("Simple.GreedFall"),
        DisplayName = "GreedFall",
        NexusModsGameId = NexusModsGameId.From(2945),
        Stores = new StoreIdentifiers(GameId.From("Simple.GreedFall")) { SteamAppIds = [606880] },
        Executable = "GreedFall.exe",
        ModRoot = new ModLocation(ModLocationRoot.GameFolder, "datalocal"),
        Install = AsIs,
    };

    /// <summary>From Vortex.</summary>
    public static readonly SimpleGameDefinition GrimDawn = new()
    {
        GameId = GameId.From("Simple.GrimDawn"),
        DisplayName = "Grim Dawn",
        NexusModsGameId = NexusModsGameId.From(1190),
        Stores = new StoreIdentifiers(GameId.From("Simple.GrimDawn")) { SteamAppIds = [219990] },
        Executable = "Grim Dawn.exe",
        ModRoot = new ModLocation(ModLocationRoot.GameFolder, "mods"),
        Install = AsIs,
    };

    /// <summary>From Vortex.</summary>
    public static readonly SimpleGameDefinition StateOfDecay = new()
    {
        GameId = GameId.From("Simple.StateOfDecay"),
        DisplayName = "State of Decay",
        NexusModsGameId = NexusModsGameId.From(223),
        Stores = new StoreIdentifiers(GameId.From("Simple.StateOfDecay")) { SteamAppIds = [241540] },
        Executable = "StateOfDecay.exe",
        ModRoot = new ModLocation(ModLocationRoot.GameFolder, "game"),
        Install = AsIs,
    };

    /// <summary>From Vortex.</summary>
    public static readonly SimpleGameDefinition XRebirth = new()
    {
        GameId = GameId.From("Simple.XRebirth"),
        DisplayName = "X Rebirth",
        NexusModsGameId = NexusModsGameId.From(154),
        Stores = new StoreIdentifiers(GameId.From("Simple.XRebirth")) { SteamAppIds = [2870] },
        Executable = "XRebirth.exe",
        ModRoot = new ModLocation(ModLocationRoot.GameFolder, "extensions"),
        Install = AsIs,
    };

    /// <summary>From Vortex.</summary>
    public static readonly SimpleGameDefinition TeamFortress2 = new()
    {
        GameId = GameId.From("Simple.TeamFortress2"),
        DisplayName = "Team Fortress 2",
        NexusModsGameId = NexusModsGameId.From(322),
        Stores = new StoreIdentifiers(GameId.From("Simple.TeamFortress2")) { SteamAppIds = [440] },
        Executable = "tf_win64.exe",
        ModRoot = new ModLocation(ModLocationRoot.GameFolder, "tf/custom"),
        Install = AsIs,
    };

    /// <summary>From Vortex.</summary>
    public static readonly SimpleGameDefinition Starbound = new()
    {
        GameId = GameId.From("Simple.Starbound"),
        DisplayName = "Starbound",
        NexusModsGameId = NexusModsGameId.From(242),
        Stores = new StoreIdentifiers(GameId.From("Simple.Starbound")) { SteamAppIds = [211820] },
        Executable = "win64/starbound.exe",
        ModRoot = new ModLocation(ModLocationRoot.GameFolder, "mods"),
        Install = AsIs,
    };

    public static readonly IReadOnlyList<SimpleGameDefinition> All =
    [
        DarkestDungeon,
        DungeonSiege,
        DungeonSiege2,
        DarkSouls,
        DarkSoulsRemastered,
        DarkSouls2Sotfs,
        DragonsDogmaDarkArisen,
        DarkMessiah,
        X4Foundations,
        KerbalSpaceProgram,
        VampireBloodlines,
        Control,
        BatmanArkhamCity,
        AssettoCorsa,
        Borderlands,
        F123,
        KarrynsPrison,
        MetalGearSolid2MasterCollection,
        MetalGearSolid3MasterCollection,
        MirrorsEdge,
        NierAutomata,
        TestDriveUnlimited2,
        TrackmaniaUnitedForever,
        TrainSimulator,
        ValkyriaChronicles,
        ZeusAndPoseidon,
        AHatInTime,
        BreakingWheel,
        DawnOfMan,
        Elex,
        GreedFall,
        GrimDawn,
        StateOfDecay,
        XRebirth,
        TeamFortress2,
        Starbound,
    ];
}

