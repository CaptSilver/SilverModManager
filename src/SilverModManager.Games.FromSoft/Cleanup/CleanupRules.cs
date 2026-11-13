namespace SilverModManager.Games.FromSoft.Cleanup;

/// <summary>
/// What the game-folder cleanup recognises as left behind by another mod manager. The mod loader names are FromSoft-wide
/// and live in <see cref="ModEngine2"/>; a game adds its own launcher, mods folder and leftovers on top.
/// </summary>
/// <param name="Launcher">The anti-cheat launcher Steam starts. Null for a game without one, which skips the launcher swap.</param>
/// <param name="LauncherBackupSuffix">Appended to the launcher's name for the copy an old launcher swap left behind.</param>
/// <param name="ModsFolder">
/// The folder other managers kept one subfolder per mod in, whose <c>.ini</c> files are copied into the loadout before
/// it is deleted. Null for a game without that convention, which skips the mod steps.
/// </param>
/// <param name="OverrideTree">
/// The top-level folder the mod loader reads override files from. It can hold a user's own files, so it is set aside
/// under the installation's cleanup folder instead of deleted. Null for a game without one.
/// </param>
/// <param name="Leftovers">Top-level names that only another mod manager puts in the game folder.</param>
public sealed record CleanupRules(
    string? Launcher,
    string LauncherBackupSuffix,
    string? ModsFolder,
    string? OverrideTree,
    string[] Leftovers)
{
    /// <summary>
    /// The mod loaders' and ReShade's leftovers and nothing game-specific: no launcher, no mods folder. ModEngine2's
    /// <c>mod/</c> is its override tree; Mod Engine 1's <c>modengine.ini</c> is read for its tree before it is deleted.
    /// </summary>
    public static readonly CleanupRules ModEngine2 = new(
        null,
        ".erm-backup",
        null,
        "mod",
        ["ReShade2.ini", "ReShade.log", "dinput8.dll", "winhttp.dll", "modengine2.dll", "modengine.toml", "modengine.ini"]);
}
