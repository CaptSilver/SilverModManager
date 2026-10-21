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
/// What is wrong with a FromSoft install, as pure functions of what is on disk. Health Check and the doctor
/// report both show these, so the two cannot disagree. The game folder checks keep the state names and the
/// verdicts of the launcher script's doctor, except where the overlay launch changes what is safe. The loader's own
/// check is <see cref="IFromSoftModLoader.HostCheck"/>.
/// </summary>
public static class FromSoftChecks
{
    private const string ModRegulationFolder = "mod";

    /// <summary>The artifacts, spawner and anti-cheat checks of one <c>Game</c> folder.</summary>
    public static IReadOnlyList<CheckResult> GameFolder(AbsolutePath gameFolder, GameFolderCheckRules rules)
    {
        if (!gameFolder.DirectoryExists())
            return [new CheckResult("artifacts", CheckLevel.Warn, $"The game folder {gameFolder} does not exist, so it was not checked.")];

        // Windows names are case-insensitive, so a proxy DLL called DINPUT8.DLL loads all the same.
        var files = gameFolder.EnumerateFiles(recursive: false).Select(f => f.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var folders = gameFolder.EnumerateDirectories(recursive: false).Select(d => d.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var launcher = rules.Launcher is not null && files.Contains(rules.Launcher);
        var hardened = rules.Launcher is not null && files.Contains(LauncherBackup(rules));
        var results = new List<CheckResult> { Artifacts(gameFolder, rules, files, folders, launcher, hardened) };

        // A hardened install has the game's own executable under the launcher's name, so the launcher rules
        // below would call it armed; the artifacts check already fails it.
        if (rules.Launcher is not null && !hardened) results.Add(new CheckResult("eac", CheckLevel.Info, $"EAC state: {EacState(files, launcher)}"));

        foreach (var spawner in SpawnersIn(gameFolder, rules))
            results.Add(new CheckResult("spawners", CheckLevel.Warn, $"Item-spawner mod present: {spawner}. Never take its save to vanilla online."));

        return results;
    }

    /// <summary>The name an old launcher swap left the original launcher under. Throws for rules without a launcher, which have no swap to undo.</summary>
    public static string LauncherBackup(GameFolderCheckRules rules)
        => rules.Launcher is null ? throw new InvalidOperationException("The rules have no launcher, so there is no backup name.") : rules.Launcher + rules.LauncherBackupSuffix;

    private static CheckResult Artifacts(AbsolutePath gameFolder, GameFolderCheckRules rules, HashSet<string> files, HashSet<string> folders, bool launcher, bool hardened)
    {
        var found = new List<string>();
        found.AddRange(rules.ModLoaderFiles.Where(files.Contains));
        if (folders.Contains(ModRegulationFolder) && (gameFolder / ModRegulationFolder).EnumerateFiles(recursive: false).Any(f => f.FileName.Equals("regulation.bin", StringComparison.OrdinalIgnoreCase)))
            found.Add("mod/regulation.bin");
        found.AddRange(rules.ExtraArtifacts.Where(files.Contains));
        found.AddRange(rules.ModFolders.Where(folders.Contains).Select(f => f + "/"));

        if (hardened)
        {
            var swap = $"{LauncherBackup(rules)} is present, so the install still carries the old launcher swap. Follow the cleanup steps to restore the original launcher.";
            return new CheckResult("artifacts", CheckLevel.Fail, found.Count == 0 ? swap : $"{swap} Mod files are also in the game folder: {string.Join(", ", found)}.");
        }

        if (found.Count == 0) return new CheckResult("artifacts", CheckLevel.Ok, "No mod loader files or mod folders in the game folder.");

        return launcher
            ? new CheckResult("artifacts", CheckLevel.Fail, $"{rules.Launcher} is present and so are mod files ({string.Join(", ", found)}): an online vanilla launch could load a mod.")
            : new CheckResult("artifacts", CheckLevel.Warn, $"Mod files are in the game folder: {string.Join(", ", found)}.");
    }

    private static string EacState(HashSet<string> files, bool launcher)
    {
        if (!launcher) return "absent";
        return GameFolderCheckRules.ProxyDlls.Any(files.Contains) ? "disarmed" : "armed";
    }

    private static IEnumerable<string> SpawnersIn(AbsolutePath gameFolder, GameFolderCheckRules rules)
        => rules.Spawners.Length == 0
            ? []
            : gameFolder.EnumerateFiles(recursive: true)
                .Select(f => f.FileName)
                .Where(name => rules.Spawners.Contains(name, StringComparer.OrdinalIgnoreCase))
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

    /// <summary>The unresolved findings of modded sessions, or null when there are none.</summary>
    public static CheckResult? Upperdir(IReadOnlyList<AuditFinding> pending)
        => pending.Count == 0
            ? null
            : new CheckResult("upperdir", CheckLevel.Warn, $"{pending.Count} unresolved finding(s) from a modded session: {string.Join(", ", pending.Select(f => f.Path))}.");
}
