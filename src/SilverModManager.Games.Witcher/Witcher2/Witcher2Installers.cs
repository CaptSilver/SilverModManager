using NexusMods.Paths;
using NexusMods.Paths.Utilities;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Games.Generic.Installers;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.Witcher.Witcher2;

internal static class Witcher2Installers
{
    public static ILibraryItemInstaller[] Create(IServiceProvider provider) =>
    [
        // First: a cook.hash marks a UserContent mod, and its .dzip files would otherwise match the CookedPC target below.
        new PredicateBasedInstaller(provider)
        {
            Root = node => node.HasDirectChild("cook.hash"),
            Destination = new GamePath(LocationId.Game, "UserContent"),
        },
        new GenericPatternMatchInstaller(provider)
        {
            InstallFolderTargets =
            [
                new InstallFolderTarget
                {
                    DestinationGamePath = new GamePath(LocationId.Game, "CookedPC"),
                    KnownSourceFolderNames = ["CookedPC"],
                    KnownValidFileExtensions = [new(".dzip"), new(".w2strings"), new(".w2speech")],
                    FileExtensionsToDiscard = [KnownExtensions.Txt, KnownExtensions.Md, KnownExtensions.Png, KnownExtensions.Jpg],
                },
            ],
        },
    ];
}
