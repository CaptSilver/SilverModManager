using DynamicData.Kernel;
using NexusMods.Paths;
using NexusMods.Paths.Utilities;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.Games.Generic.Simple;

/// <summary>Every game that is a <see cref="SimpleGameDefinition"/> row. Adding a game is adding a row.</summary>
public static class SimpleGames
{
    private static readonly GameId Witcher1Id = GameId.From("Witcher.Witcher1");
    private static readonly GameId Witcher2Id = GameId.From("Witcher.Witcher2");

    // Aurora resource types the game reads from Data/Override. Anything else in an archive is not a mod file.
    private static readonly Extension[] Witcher1OverrideExtensions =
    [
        new(".2da"), new(".tlk"), new(".dlg"), new(".gff"), new(".mdb"), new(".utc"), new(".uti"), new(".are"), new(".git"), new(".ncs"), new(".nss"), new(".ssf"), new(".txi"),
        new(".dds"), new(".tga"), new(".mat"), new(".naf"), new(".wav"), new(".ogg"),
    ];

    public static readonly SimpleGameDefinition Witcher1 = new()
    {
        GameId = Witcher1Id,
        DisplayName = "The Witcher",
        NexusModsGameId = NexusModsGameId.From(150),
        Stores = new StoreIdentifiers(Witcher1Id) { SteamAppIds = [20900] },
        Executable = "System/witcher.exe",
        ModRoot = new ModLocation(ModLocationRoot.GameFolder, "Data/Override"),
        Install = new SimpleInstallRules
        {
            Targets =
            [
                new SimpleInstallTarget
                {
                    Destination = RelativePath.Empty,
                    KnownSourceFolderNames = ["Override"],
                    KnownValidFileExtensions = Witcher1OverrideExtensions,
                    FileExtensionsToDiscard = [KnownExtensions.Txt, KnownExtensions.Md, KnownExtensions.Pdf, KnownExtensions.Png, KnownExtensions.Jpg],
                },
            ],
        },
    };

    public static readonly SimpleGameDefinition Witcher2 = new()
    {
        GameId = Witcher2Id,
        DisplayName = "The Witcher 2: Assassins of Kings",
        NexusModsGameId = NexusModsGameId.From(153),
        Stores = new StoreIdentifiers(Witcher2Id) { SteamAppIds = [20920] },
        Executable = "bin/witcher2.exe",
        // The game reads both CookedPC and UserContent from its own folder.
        ModRoot = new ModLocation(ModLocationRoot.GameFolder, RelativePath.Empty),
        Install = new SimpleInstallRules
        {
            // A cook.hash marks a UserContent mod, and its .dzip files would otherwise match the CookedPC target.
            // The ignored extensions keep a readme outside the cook.hash folder from making the installer decline.
            MarkerRules =
            [
                new SimpleMarkerRule
                {
                    MarkerFile = "cook.hash",
                    Destination = "UserContent",
                    IgnoredExtensions = [KnownExtensions.Txt, KnownExtensions.Md, KnownExtensions.Png, KnownExtensions.Jpg],
                },
            ],
            Targets =
            [
                new SimpleInstallTarget
                {
                    Destination = "CookedPC",
                    KnownSourceFolderNames = ["CookedPC"],
                    KnownValidFileExtensions = [new(".dzip"), new(".w2strings"), new(".w2speech")],
                    FileExtensionsToDiscard = [KnownExtensions.Txt, KnownExtensions.Md, KnownExtensions.Png, KnownExtensions.Jpg],
                },
            ],
        },
    };

    public static readonly IReadOnlyList<SimpleGameDefinition> All = [Witcher1, Witcher2, .. GameFolderSimpleGames.All, .. KnownFolderSimpleGames.All];
}
