namespace NexusMods.Games.Unity.BepInEx;

/// <summary>Names of the folders and files BepInEx lays out in a game folder.</summary>
internal static class BepInExFiles
{
    public const string Root = "BepInEx";
    public const string Plugins = "plugins";
    public const string Patchers = "patchers";
    public const string Core = "core";
    public const string Monomod = "monomod";
    public const string Config = "config";

    public const string DllExtension = ".dll";
    public const string MonomodExtension = ".mm.dll";

    /// <summary>The proxy DLL the loader pack puts next to the game executable.</summary>
    public const string DoorstopProxy = "winhttp.dll";
    public const string DoorstopConfig = "doorstop_config.ini";
    public const string Preloader = "BepInEx.Preloader.dll";
}
