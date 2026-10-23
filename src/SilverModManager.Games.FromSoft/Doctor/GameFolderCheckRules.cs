namespace SilverModManager.Games.FromSoft.Doctor;

/// <summary>
/// What the game folder checks look for in one game's <c>Game</c> folder. The mod loader files and the
/// <c>mod/regulation.bin</c> rule are FromSoft-wide and live in the checks; a game adds its own launcher, mod folders
/// and spawners on top of <see cref="ModEngine2"/>.
/// </summary>
/// <param name="Launcher">The anti-cheat launcher Steam starts. Null for a game without one, which skips the launcher state and the armed wording.</param>
/// <param name="LauncherBackupSuffix">Appended to the launcher's name for the copy an old launcher swap left behind.</param>
/// <param name="ModLoaderFiles">Files that only a mod loader puts in the game folder, such as proxy DLLs.</param>
/// <param name="ModFolders">Folders that only mods put in the game folder.</param>
/// <param name="ExtraArtifacts">Other files that only mods put in the game folder.</param>
/// <param name="Spawners">DLLs that create items, wherever they are in the game folder.</param>
public sealed record GameFolderCheckRules(
    string? Launcher,
    string LauncherBackupSuffix,
    string[] ModLoaderFiles,
    string[] ModFolders,
    string[] ExtraArtifacts,
    string[] Spawners)
{
    /// <summary>The proxy DLLs mod loaders hook the game through; with the launcher present they disarm its anti-cheat.</summary>
    public static readonly string[] ProxyDlls = ["dinput8.dll", "winhttp.dll"];

    /// <summary>ModEngine2's files and nothing game-specific: no launcher, no extra folders, no spawners.</summary>
    public static readonly GameFolderCheckRules ModEngine2 = new(null, ".erm-backup", [..ProxyDlls, "modengine2.dll", "modengine.toml"], [], [], []);
}
