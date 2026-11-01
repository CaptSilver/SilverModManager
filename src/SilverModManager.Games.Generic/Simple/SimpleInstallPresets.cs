using NexusMods.Paths;
using NexusMods.Paths.Utilities;

namespace SilverModManager.Games.Generic.Simple;

/// <summary>
/// Install rules shared by the simple game tables, so a layout several games use is written once.
/// </summary>
public static class SimpleInstallPresets
{
    // Games whose plugin or extension checks no layout take any archive, laid out like the mod folder.
    public static readonly SimpleInstallRules AsIs = new() { InstallAsIs = true };

    // Mod Organizer 2 accepts an archive when one of its top-level folders is on the game's list, and offers to unwrap
    // an archive that has the folder one level down. The folder holding a listed one is the root here.
    public static SimpleInstallRules FolderNamed(string[] folders) => new()
    {
        Targets = [new SimpleInstallTarget { Destination = RelativePath.Empty, KnownSubfolderNames = folders.Select(folder => (RelativePath)folder).ToArray() }],
    };

    // Vortex puts a mod with the marker file in a folder of its own and drops what lies outside the marker's folder, so
    // a readme beside the mod folder does not make the archive unusable.
    // A game that lists mods by an id inside the marker passes the namer that reads it.
    public static SimpleInstallRules InOwnFolderByMarker(string markerFile, IModFolderNameFromMarker? folderNameFromMarker = null) => new()
    {
        MarkerRules =
        [
            new SimpleMarkerRule
            {
                MarkerFile = markerFile,
                Destination = RelativePath.Empty,
                InFolderNamedAfterMod = true,
                FolderNameFromMarker = folderNameFromMarker,
                IgnoredExtensions = [KnownExtensions.Txt, KnownExtensions.Md, KnownExtensions.Png, KnownExtensions.Jpg],
            },
        ],
    };

    // Vortex takes the folder holding the first file with the extension as the mod's root, and installs it unchanged.
    public static SimpleInstallRules FolderOfFirstFile(string extension) => new()
    {
        Targets = [new SimpleInstallTarget { Destination = RelativePath.Empty, KnownValidFileExtensions = [new Extension(extension)] }],
    };

    public static SimpleInstallRules StopFolders(string[] folders) => FolderNamed(folders) with { InstallAsIs = true };

    // A Unity Mod Manager mod has an Info.json and is one folder; anything else is laid out like the Mods folder.
    public static readonly SimpleInstallRules UnityModManagerMod = InOwnFolderByMarker("Info.json") with { InstallAsIs = true };
}
