using NexusMods.Paths;
using NexusMods.Sdk.Games;

namespace NexusMods.Games.FromSoft.EldenRing;

/// <summary>
/// Elden Ring's path constants, and its <see cref="FromSoftPaths"/> rules with its layout filled in. The rules
/// themselves live in <see cref="FromSoftPaths"/>; this is the facade for code that only ever deals with Elden Ring.
/// </summary>
public static class EldenRingPaths
{
    public const uint SteamAppId = 1245620;

    /// <summary>The folder inside the Steam install folder that holds the game's files.</summary>
    public const string GameSubfolder = "Game";

    /// <summary>The game's own executable, in <see cref="GameSubfolder"/>.</summary>
    public const string GameExecutable = "eldenring.exe";

    /// <summary>The anti-cheat launcher Steam starts, in <see cref="GameSubfolder"/>; it starts <see cref="GameExecutable"/>.</summary>
    public const string AntiCheatLauncher = "start_protected_game.exe";

    /// <summary>The save file the game writes.</summary>
    public const string VanillaSave = "ER0000.sl2";

    /// <summary>The save file Seamless Co-op writes.</summary>
    public const string SeamlessCoopSave = "ER0000.co2";

    /// <summary>The save file a modded session is pointed at, so it never opens <see cref="VanillaSave"/>.</summary>
    public const string ModdedSave = "ER0000_silver.sl2";

    /// <summary>The folder with the game's executable and archives, <c>&lt;install&gt;/Game</c>.</summary>
    public static AbsolutePath GameFolder(GameInstallation installation) => FromSoftPaths.GameFolder(Layout, installation);

    /// <summary>The folder the game keeps every Steam account's saves in, inside a Proton prefix.</summary>
    public static AbsolutePath SaveRoot(AbsolutePath prefixPath) => FromSoftPaths.SaveRoot(Layout, prefixPath);

    /// <summary>The folder the game keeps one Steam account's saves in, inside a Proton prefix.</summary>
    public static AbsolutePath SaveFolder(AbsolutePath prefixPath, ulong steamId64)
        => FromSoftPaths.SaveFolder(Layout, prefixPath, steamId64);

    /// <summary>
    /// The name of an installation's folder under <see cref="Me3Root"/>: its Steam app id, and the game's own app id
    /// for an install that did not come from Steam.
    /// </summary>
    public static string InstallationFolderName(GameLocatorResult locatorResult)
        => FromSoftPaths.InstallationFolderName(Layout, locatorResult);

    /// <summary>The folder every Elden Ring installation's host-side files live under.</summary>
    public static AbsolutePath Me3Root(FromSoftSettings settings, IFileSystem fileSystem)
        => FromSoftPaths.DataRoot(Layout, settings, fileSystem);

    /// <summary>The folder mod packages and natives of one installation deploy to.</summary>
    public static AbsolutePath Me3Mods(FromSoftSettings settings, IFileSystem fileSystem, string storeIdentifier)
        => FromSoftPaths.InstallationRoot(Layout, settings, fileSystem, storeIdentifier) / "mods";

    /// <summary>The folder the mod loader of one installation deploys to.</summary>
    public static AbsolutePath Me3Host(FromSoftSettings settings, IFileSystem fileSystem, string storeIdentifier)
        => FromSoftPaths.InstallationRoot(Layout, settings, fileSystem, storeIdentifier) / "me3";

    /// <summary>
    /// The folder that stands in for the user's home for me3: its cache, profiles, logs and data are kept here
    /// instead of in <c>~/.cache/me3</c>, <c>~/.config/me3</c> and <c>~/.local/share/me3</c>.
    /// </summary>
    public static AbsolutePath Me3Home(FromSoftSettings settings, IFileSystem fileSystem, string storeIdentifier)
        => FromSoftPaths.InstallationRoot(Layout, settings, fileSystem, storeIdentifier) / "me3-home";

    /// <summary>The four XDG folders inside <see cref="Me3Home"/>, which are the only places me3 may write outside the game's own folders.</summary>
    public static AbsolutePath[] Me3HomeFolders(AbsolutePath home) => [home / "cache", home / "config", home / "data", home / "state"];

    /// <summary>The XDG variables that point me3 at <see cref="Me3Home"/>.</summary>
    public static Dictionary<string, string> Me3HomeEnvironment(AbsolutePath home) => new()
    {
        ["XDG_CACHE_HOME"] = (home / "cache").GetFullPath(),
        ["XDG_CONFIG_HOME"] = (home / "config").GetFullPath(),
        ["XDG_DATA_HOME"] = (home / "data").GetFullPath(),
        ["XDG_STATE_HOME"] = (home / "state").GetFullPath(),
    };

    /// <summary>The folder one installation's cleanup records live in, such as the links it removed from the game folder.</summary>
    public static AbsolutePath CleanupFolder(FromSoftSettings settings, IFileSystem fileSystem, string storeIdentifier)
        => FromSoftPaths.CleanupFolder(Layout, settings, fileSystem, storeIdentifier);

    /// <summary>Whether the path is inside the <c>Game</c> folder, which the app never writes to.</summary>
    public static bool IsInGameFolder(GamePath path) => FromSoftPaths.IsInGameFolder(Layout, path);

    /// <summary>Whether indexing skips the path; see <see cref="FromSoftPaths.IsArchiveOrMedia"/>.</summary>
    public static bool IsArchiveOrMedia(GamePath path) => FromSoftPaths.IsArchiveOrMedia(Layout, path);

    private static FromSoftGameLayout Layout => EldenRingGame.Definition.Layout;
}
