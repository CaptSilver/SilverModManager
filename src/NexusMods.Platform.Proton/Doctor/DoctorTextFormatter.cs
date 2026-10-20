using System.Globalization;

namespace NexusMods.Platform.Proton.Doctor;

/// <summary>
/// The human-readable form of a <see cref="DoctorReport"/>.
/// </summary>
public static class DoctorTextFormatter
{
    public static IReadOnlyList<string> Format(DoctorReport report)
    {
        if (report.Installations.Count == 0) return ["No managed games. Create a loadout for a game first."];

        var lines = new List<string>();
        foreach (var install in report.Installations)
        {
            if (lines.Count > 0) lines.Add("");
            lines.Add($"{install.Game} ({install.Store} {install.StoreId})");
            lines.Add($"  Loadouts: {string.Join(", ", install.Loadouts)}");
            lines.Add($"  Prefix: {Prefix(install.Prefix)}");
            if (install.Proton is { } proton) lines.AddRange(Proton(proton));
            lines.Add($"  Runner: {Runner(install.Runner)}");
            if (install.LaunchOptions is { } options) lines.Add($"  Launch options: {LaunchOptions(options)}");
            if (install.Deployment.Count > 0) lines.Add($"  Deployment: {string.Join(", ", install.Deployment.Select(Deployment))}");
            lines.Add($"  Launch: {Lock(install.Lock)}");
        }
        return lines;
    }

    private static string Prefix(DoctorPrefix prefix) => prefix.Status switch
    {
        DoctorPrefixStatus.Resolved => $"{prefix.Path} ({prefix.Kind})",
        DoctorPrefixStatus.Native => $"none needed, {prefix.Message}",
        _ => $"not found, {prefix.Message}",
    };

    private static IEnumerable<string> Proton(DoctorProton proton)
    {
        yield return proton.Selected is { } selected
            ? $"  Proton: {selected.Name} ({selected.Directory})"
            : $"  Proton: none chosen, {proton.Diagnostic}";
        yield return $"  Proton builds: {(proton.Builds.Count == 0 ? "none" : string.Join(", ", proton.Builds.Select(b => b.Name)))}";
    }

    private static string Runner(DoctorRunner runner) => runner.CanRun
        ? $"{runner.Strategy}: {string.Join(' ', runner.Argv)}"
        : $"cannot run tools, {runner.Diagnostic}";

    private static string LaunchOptions(DoctorLaunchOptions options) => options.Status switch
    {
        DoctorLaunchOptionsStatus.NoConfig => $"no readable {options.ConfigPath}",
        DoctorLaunchOptionsStatus.NoOptions => "none set",
        DoctorLaunchOptionsStatus.NoSpan => $"set without the launcher span: {options.Line}",
        _ => $"launcher span for loadout '{options.Loadout}': {options.Line}",
    };

    private static string Deployment(DoctorDeploymentRoot root)
        => $"{root.Root} -> {root.Backend}{(root.Recorded ? "" : " (default)")}{(root.Registered ? "" : " (no such backend)")}";

    private static string Lock(DoctorLock state) => state.Status switch
    {
        DoctorLockStatus.Free => "not running",
        DoctorLockStatus.Session => $"in progress{Holder(state)}",
        DoctorLockStatus.Leftover => $"not running, a launch died without cleaning up{Holder(state)}",
        _ => $"cannot inspect, {state.Diagnostic}",
    };

    private static string Holder(DoctorLock state) => state.Pid is { } pid
        ? string.Create(CultureInfo.InvariantCulture, $" (pid {pid}, loadout '{state.Loadout}', {state.Phase})")
        : "";
}
