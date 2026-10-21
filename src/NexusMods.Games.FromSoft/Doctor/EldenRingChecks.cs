using NexusMods.Games.FromSoft.EldenRing;
using NexusMods.Games.FromSoft.Overlay;
using NexusMods.Paths;

namespace NexusMods.Games.FromSoft.Doctor;

/// <summary>How serious a check result is.</summary>
public enum CheckLevel
{
    Ok,
    Info,
    Warn,
    Fail,
}

/// <param name="Id">What was checked: artifacts, spawners, eac, overlay, me3 or upperdir.</param>
/// <param name="Level">How serious the result is.</param>
/// <param name="Message">The result in a sentence.</param>
public sealed record CheckResult(string Id, CheckLevel Level, string Message);

/// <summary>
/// What is wrong with an Elden Ring install, as pure functions of what is on disk. Health Check and the doctor
/// report both show these, so the two cannot disagree. The game folder checks keep the state names and the
/// verdicts of the launcher script's doctor, except where the overlay launch changes what is safe.
/// </summary>
public static class EldenRingChecks
{
    private static readonly string[] ProxyDlls = ["dinput8.dll", "winhttp.dll"];
    private static readonly string[] ModLoaderFiles = [..ProxyDlls, "modengine2.dll", "modengine.toml"];
    private static readonly string[] ModFolders = ["SeamlessCoop", "mods"];
    private static readonly string[] Spawners = ["ermerchant.dll", "erdtools.dll", "glorious_merchant.dll"];

    private const string SeamlessCoopLauncher = "ersc_launcher.exe";
    internal const string OldLauncherBackup = EldenRingPaths.AntiCheatLauncher + ".erm-backup";

    /// <summary>The artifacts, spawner and anti-cheat checks of one <c>Game</c> folder.</summary>
    public static IReadOnlyList<CheckResult> GameFolder(AbsolutePath gameFolder)
    {
        if (!gameFolder.DirectoryExists())
            return [new CheckResult("artifacts", CheckLevel.Warn, $"The game folder {gameFolder} does not exist, so it was not checked.")];

        // Windows names are case-insensitive, so a proxy DLL called DINPUT8.DLL loads all the same.
        var files = gameFolder.EnumerateFiles(recursive: false).Select(f => f.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var folders = gameFolder.EnumerateDirectories(recursive: false).Select(d => d.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var launcher = files.Contains(EldenRingPaths.AntiCheatLauncher);
        var hardened = files.Contains(OldLauncherBackup);
        var results = new List<CheckResult> { Artifacts(gameFolder, files, folders, launcher, hardened) };

        // A hardened install has the game's own executable under the launcher's name, so the launcher rules
        // below would call it armed; the artifacts check already fails it.
        if (!hardened) results.Add(new CheckResult("eac", CheckLevel.Info, $"EAC state: {EacState(files, launcher)}"));

        foreach (var spawner in SpawnersIn(gameFolder))
            results.Add(new CheckResult("spawners", CheckLevel.Warn, $"Item-spawner mod present: {spawner}. Never take its save to vanilla online."));

        return results;
    }

    private static CheckResult Artifacts(AbsolutePath gameFolder, HashSet<string> files, HashSet<string> folders, bool launcher, bool hardened)
    {
        var found = new List<string>();
        found.AddRange(ModLoaderFiles.Where(files.Contains));
        if (folders.Contains("mod") && (gameFolder / "mod").EnumerateFiles(recursive: false).Any(f => f.FileName.Equals("regulation.bin", StringComparison.OrdinalIgnoreCase)))
            found.Add("mod/regulation.bin");
        if (files.Contains(SeamlessCoopLauncher)) found.Add(SeamlessCoopLauncher);
        found.AddRange(ModFolders.Where(folders.Contains).Select(f => f + "/"));

        var swap = $"{OldLauncherBackup} is present, so the install still carries the old launcher swap. Follow the cleanup steps to restore the original launcher.";
        if (hardened)
            return new CheckResult("artifacts", CheckLevel.Fail, found.Count == 0 ? swap : $"{swap} Mod files are also in the game folder: {string.Join(", ", found)}.");

        if (found.Count == 0) return new CheckResult("artifacts", CheckLevel.Ok, "No mod loader files or mod folders in the game folder.");

        return launcher
            ? new CheckResult("artifacts", CheckLevel.Fail, $"{EldenRingPaths.AntiCheatLauncher} is present and so are mod files ({string.Join(", ", found)}): an online vanilla launch could load a mod.")
            : new CheckResult("artifacts", CheckLevel.Warn, $"Mod files are in the game folder: {string.Join(", ", found)}.");
    }

    private static string EacState(HashSet<string> files, bool launcher)
    {
        if (!launcher) return "absent";
        return ProxyDlls.Any(files.Contains) ? "disarmed" : "armed";
    }

    private static IEnumerable<string> SpawnersIn(AbsolutePath gameFolder)
        => gameFolder.EnumerateFiles(recursive: true)
            .Select(f => f.FileName)
            .Where(name => Spawners.Contains(name, StringComparer.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal);

    /// <summary>Whether bwrap is on <c>PATH</c> and can make a user namespace, the same questions the launch asks.</summary>
    public static CheckResult OverlaySupport(IExecutableLocator executables, ISandboxProbe probe)
    {
        if (executables.Find(OverlayCommand.Program) is not { } bwrap)
            return new CheckResult("overlay", CheckLevel.Fail, "bwrap was not found on PATH. Install bubblewrap; a modded launch needs it to keep the game folder from being written to.");

        return probe.Probe(bwrap) is { } failure
            ? new CheckResult("overlay", CheckLevel.Fail, $"bwrap cannot make a user namespace here: {failure}")
            : new CheckResult("overlay", CheckLevel.Ok, $"bwrap can make a user namespace ({bwrap}).");
    }

    /// <param name="binary">Where the me3 binary is deployed.</param>
    /// <param name="executable">Whether it is there and executable.</param>
    /// <param name="version">What it printed for <c>--version</c>; null when it was not run or printed nothing.</param>
    public static CheckResult Me3(AbsolutePath binary, bool executable, string? version)
    {
        if (!executable)
            return new CheckResult("me3", CheckLevel.Warn, $"me3 is not deployed or not executable ({binary}). Only a loadout with mods needs it.");

        return string.IsNullOrWhiteSpace(version)
            ? new CheckResult("me3", CheckLevel.Warn, $"me3 at {binary} did not report a version: it failed to start, exited with an error or printed nothing.")
            : new CheckResult("me3", CheckLevel.Ok, $"{version.Trim()} ({binary})");
    }

    /// <summary>The unresolved findings of modded sessions, or null when there are none.</summary>
    public static CheckResult? Upperdir(IReadOnlyList<AuditFinding> pending)
        => pending.Count == 0
            ? null
            : new CheckResult("upperdir", CheckLevel.Warn, $"{pending.Count} unresolved finding(s) from a modded session: {string.Join(", ", pending.Select(f => f.Path))}.");
}
