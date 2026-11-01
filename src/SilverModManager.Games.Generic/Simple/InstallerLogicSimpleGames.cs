using System.Collections.Immutable;
using NexusMods.Paths;
using NexusMods.Paths.Utilities;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.NexusModsApi;

using static SilverModManager.Games.Generic.Simple.SimpleInstallPresets;

namespace SilverModManager.Games.Generic.Simple;

/// <summary>
/// Simple games whose mod archives need more than a folder to drop files in: a rule that sorts files by kind, or a
/// refusal of archives that only work with a program the app does not run. Executables, store ids and layouts are
/// read from the Vortex game extensions and the Mod Organizer 2 plugins (each row says which); Nexus ids come from
/// the Nexus games list, https://data.nexusmods.com/file/nexus-data/games.json.
/// </summary>
public static class InstallerLogicSimpleGames
{
    // The game's own folders, from Vortex's KOTOR extension. A mod archive holding one is laid out like the game folder.
    private static readonly string[] KotorFolders =
    [
        "data", "lips", "miles", "modules", "movies", "override", "rims", "saves", "streammusic", "streamsounds", "streamvoice", "streamwaves", "texturepack",
    ];

    // Vortex installs only what lies at or below one of those folders, so a readme beside them stays out.
    // Folders a Neverwinter Nights archive may already be laid out in, from Vortex's list of moddable directories.
    private static readonly string[] NeverwinterFolders =
    [
        "ambient", "database", "development", "dmvault", "hak", "localvault", "logs", "modules", "movies", "music", "nwsync", "override", "portraits",
        "servervault", "tempclient", "tlk",
    ];

    private static SimpleExtensionRoute Flat(string folder, params string[] extensions) =>
        new() { Destination = folder, Extensions = extensions.Select(extension => new Extension(extension)).ToArray() };

    // Vortex's extension table: loose files go to the folder of their kind.
    private static readonly SimpleExtensionRoute[] NeverwinterRoutes =
    [
        Flat("modules", ".mod"),
        Flat("portraits", ".tga"),
        Flat("erf", ".erf"),
        Flat("hak", ".hak", ".exe", ".hif"),
        Flat("tlk", ".tlk"),
        Flat("music", ".bmu"),
        Flat("ambient", ".wav"),
        Flat("database", ".cdx", ".dbf", ".fpt"),
        Flat("movies", ".nbm", ".bik"),
        Flat("override", ".2da", ".uti", ".txi", ".mdl", ".ncs", ".dlg", ".utp"),
    ];

    // Mod Organizer 2 allows a package five folders deep and a script one, and moves deeper ones up. Vortex sends the
    // files of a lot, household or room to Tray; the table of those kinds is its.
    private static readonly SimpleExtensionRoute[] Sims4Routes =
    [
        new() { Destination = "Mods", Extensions = [new Extension(".package")], KeepFolders = 5 },
        new() { Destination = "Mods", Extensions = [new Extension(".ts4script")], KeepFolders = 1 },
        Flat("Tray", ".bpi", ".blueprint", ".trayitem", ".sfx", ".ion", ".householdbinary", ".sgi", ".hhi", ".room", ".midi", ".rmi"),
    ];

    private static SimpleInstallRules Kotor => new()
    {
        Targets =
        [
            new SimpleInstallTarget
            {
                Destination = RelativePath.Empty,
                KnownSubfolderNames = KotorFolders.Select(folder => (RelativePath)folder).ToArray(),
                FileExtensionsToDiscard = [KnownExtensions.Txt, KnownExtensions.Md, KnownExtensions.Pdf, KnownExtensions.Png, KnownExtensions.Jpg],
            },
        ],
        InstallAsIs = true,
        InstallAsIsDestination = "override",
        Refusals =
        [
            new SimpleRefusalRule
            {
                FileName = "TSLPatcher.exe",
                Reason = "This is TSLPatcher itself, a separate program that patches the game's files and has to be run on its own. This app only copies files, so it can't install it.",
            },
            new SimpleRefusalRule
            {
                FolderName = "tslpatchdata",
                Reason = "This mod is installed by running TSLPatcher, which patches the game's files. This app only copies files, so it can't install it. Run the mod's own installer instead.",
            },
        ],
    };

    /// <remarks>From Vortex. Steam launch entry 0 of app 32370 is swkotor.exe.</remarks>
    public static readonly SimpleGameDefinition KnightsOfTheOldRepublic = new()
    {
        GameId = GameId.From("Simple.KnightsOfTheOldRepublic"),
        DisplayName = "Star Wars: Knights of the Old Republic",
        NexusModsGameId = NexusModsGameId.From(234),
        Stores = new StoreIdentifiers(GameId.From("Simple.KnightsOfTheOldRepublic")) { SteamAppIds = [32370], GOGProductIds = [1207666283] },
        Executable = "swkotor.exe",
        ModRoot = new ModLocation(ModLocationRoot.GameFolder, RelativePath.Empty),
        Install = Kotor,
    };

    /// <remarks>From Vortex. Steam launch entry 0 of app 208580 is swkotor2.exe; the Linux entry is a separate port.</remarks>
    public static readonly SimpleGameDefinition KnightsOfTheOldRepublic2 = new()
    {
        GameId = GameId.From("Simple.KnightsOfTheOldRepublic2"),
        DisplayName = "Star Wars: Knights of the Old Republic II - The Sith Lords",
        NexusModsGameId = NexusModsGameId.From(198),
        Stores = new StoreIdentifiers(GameId.From("Simple.KnightsOfTheOldRepublic2")) { SteamAppIds = [208580], GOGProductIds = [1421404581] },
        Executable = "swkotor2.exe",
        ModRoot = new ModLocation(ModLocationRoot.GameFolder, RelativePath.Empty),
        Install = Kotor,
    };

    /// <remarks>
    /// From Vortex's game-neverwinter-nights. Steam launch entry 0 of app 704450 is bin/win32/nwmain.exe. Nexus lists
    /// two games under the domain "neverwinter": id 180 is Neverwinter Nights, 8703 is the online game.
    /// </remarks>
    public static readonly SimpleGameDefinition NeverwinterNightsEnhancedEdition = new()
    {
        GameId = GameId.From("Simple.NeverwinterNightsEnhancedEdition"),
        DisplayName = "Neverwinter Nights: Enhanced Edition",
        NexusModsGameId = NexusModsGameId.From(180),
        Stores = new StoreIdentifiers(GameId.From("Simple.NeverwinterNightsEnhancedEdition")) { SteamAppIds = [704450] },
        Executable = "bin/win32/nwmain.exe",
        ModRoot = new ModLocation(ModLocationRoot.Documents, "Neverwinter Nights"),
        LocationNames = Named(ModLocationRoot.Documents, "Neverwinter Nights folder"),
        LocationSentenceNames = Named(ModLocationRoot.Documents, "the Neverwinter Nights folder in Documents"),
        Install = FolderNamed(NeverwinterFolders) with
        {
            ExtensionRoutes = NeverwinterRoutes,
            LeaveOutUnroutedFiles = true,
        },
    };

    /// <remarks>
    /// From Mod Organizer 2's thesims4 plugin (executable, Steam id, Mods folder, the depth limits) and Vortex's game-sims4
    /// (the Tray file kinds). The folder is the English one; Vortex also knows the German, Spanish, French and Dutch names.
    /// </remarks>
    public static readonly SimpleGameDefinition TheSims4 = new()
    {
        GameId = GameId.From("Simple.TheSims4"),
        DisplayName = "The Sims 4",
        NexusModsGameId = NexusModsGameId.From(641),
        Stores = new StoreIdentifiers(GameId.From("Simple.TheSims4")) { SteamAppIds = [1222670] },
        Executable = "Game/Bin/TS4_x64.exe",
        ModRoot = new ModLocation(ModLocationRoot.Documents, "Electronic Arts/The Sims 4"),
        LocationNames = Named(ModLocationRoot.Documents, "The Sims 4 folder"),
        LocationSentenceNames = Named(ModLocationRoot.Documents, "the Sims 4 folder in Documents"),
        Install = new SimpleInstallRules { ExtensionRoutes = Sims4Routes },
    };

    // The location ends at the folder the row names, so the player's name for it is the folder's own.
    private static IReadOnlyDictionary<string, string> Named(ModLocationRoot root, string name) =>
        ImmutableDictionary.CreateRange([KeyValuePair.Create(SimpleLocations.For(root).ToString(), name)]);

    public static readonly IReadOnlyList<SimpleGameDefinition> All = [KnightsOfTheOldRepublic, KnightsOfTheOldRepublic2, NeverwinterNightsEnhancedEdition, TheSims4];
}
