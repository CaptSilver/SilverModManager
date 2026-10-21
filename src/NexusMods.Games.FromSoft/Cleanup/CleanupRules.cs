namespace NexusMods.Games.FromSoft.Cleanup;

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
/// <param name="Leftovers">Top-level names that only another mod manager puts in the game folder.</param>
public sealed record CleanupRules(
    string? Launcher,
    string LauncherBackupSuffix,
    string? ModsFolder,
    string[] Leftovers)
{
    /// <summary>ModEngine2's and ReShade's leftovers and nothing game-specific: no launcher, no mods folder.</summary>
    public static readonly CleanupRules ModEngine2 = new(
        null,
        ".erm-backup",
        null,
        ["ReShade2.ini", "ReShade.log", "dinput8.dll", "winhttp.dll", "modengine2.dll", "modengine.toml", "mod"]);
}
