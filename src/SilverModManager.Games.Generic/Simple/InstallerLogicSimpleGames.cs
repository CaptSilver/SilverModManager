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

    public static readonly IReadOnlyList<SimpleGameDefinition> All = [KnightsOfTheOldRepublic, KnightsOfTheOldRepublic2];
}
