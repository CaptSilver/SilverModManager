using NexusMods.Paths;
using NexusMods.Paths.Utilities;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Games.Generic.Installers;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.Witcher.Witcher1;

internal static class Witcher1Installers
{
    // Aurora resource types the game reads from Data/Override. Anything else in an archive is not a mod file.
    public static readonly Extension[] OverrideExtensions =
    [
        new(".2da"), new(".tlk"), new(".dlg"), new(".gff"), new(".mdb"), new(".utc"), new(".uti"), new(".are"), new(".git"), new(".ncs"), new(".nss"), new(".ssf"), new(".txi"),
        new(".dds"), new(".tga"), new(".mat"), new(".naf"), new(".wav"), new(".ogg"),
    ];

    public static ILibraryItemInstaller[] Create(IServiceProvider provider) =>
    [
        new GenericPatternMatchInstaller(provider)
        {
            InstallFolderTargets =
            [
                new InstallFolderTarget
                {
                    DestinationGamePath = new GamePath(LocationId.Game, "Data/Override"),
                    KnownSourceFolderNames = ["Override"],
                    KnownValidFileExtensions = OverrideExtensions,
                    FileExtensionsToDiscard = [KnownExtensions.Txt, KnownExtensions.Md, KnownExtensions.Pdf, KnownExtensions.Png, KnownExtensions.Jpg],
                },
            ],
        },
    ];
}
