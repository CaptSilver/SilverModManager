
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.CreationEngine;

public static class KnownPaths
{
    public static readonly GamePath Game = new(LocationId.Game, "");
    public static readonly GamePath Data = new(LocationId.Game, "Data");
    public static readonly GamePath SKSE64Loader = new(LocationId.Game, "skse64_loader.exe");
    public static readonly GamePath SKSELoader = new(LocationId.Game, "skse_loader.exe");
    public static readonly GamePath SKSEVRLoader = new(LocationId.Game, "sksevr_loader.exe");
    public static readonly GamePath F4SELoader = new(LocationId.Game, "f4se_loader.exe");
    public static readonly GamePath F4SEVRLoader = new(LocationId.Game, "f4sevr_loader.exe");
    public static readonly GamePath SFSELoader = new(LocationId.Game, "sfse_loader.exe");
    public static readonly GamePath OBSELoader = new(LocationId.Game, "obse_loader.exe");
    // script_extender.py in Mod Organizer 2's basic_games: binaryName obse64_loader.exe, installed in OblivionRemastered\Binaries\Win64.
    public static readonly GamePath OBSE64Loader = new(LocationId.Game, "OblivionRemastered/Binaries/Win64/obse64_loader.exe");
    public static readonly GamePath FOSELoader = new(LocationId.Game, "fose_loader.exe");
    public static readonly GamePath NVSELoader = new(LocationId.Game, "nvse_loader.exe");
    
    /// <summary>
    /// Common top level folders for use in the StopPatternInstaller.
    /// </summary>
    public static readonly string[] CommonTopLevelFolders =
    [
        "asi",
        "calientetools",
        "distantlod",
        "facegen",
        "fonts",
        "interface",
        "lodsettings",
        "lsdata",
        "menus",
        "meshes",
        "music",
        "scripts",
        "shaders",
        "sound",
        "strings",
        "textures",
        "tools",
        "trees",
        "video",
    ];
}
