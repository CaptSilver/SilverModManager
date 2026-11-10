namespace SilverModManager.Games.CreationEngine.CrashLogs;

/// <summary>
/// How well a log's plugin list fits a loadout. Logs belong to the game's prefix, not to a loadout, so a log written
/// while another loadout was applied can sit in this one's Health Check.
/// </summary>
/// <param name="Absent">Names the log loaded that the loadout has no file for.</param>
/// <param name="Total">The names the log loaded.</param>
public readonly record struct LoadoutFit(int Absent, int Total)
{
    /// <summary>
    /// At least three names missing and more than one in twenty. Removing one mod, even with a handful of plugins,
    /// stays under both bars; a different loadout differs by far more.
    /// </summary>
    public bool IsAnotherLoadout => Absent >= 3 && Absent * 20 > Total;

    /// <summary>
    /// Compares the plugins the log lists, or the script extender DLLs when it lists none (a log cut off before the
    /// plugin list, or a start-up crash before any plugin loaded). Only the log-to-loadout direction counts: the
    /// loadout's side also holds plugins switched off in Plugins.txt. Null when the log lists neither.
    /// </summary>
    public static LoadoutFit? Of(CrashReport report, LoadoutFileIndex index)
    {
        if (report.Plugins.Count > 0) return Count(report.Plugins, index.PluginNames);
        if (report.SksePlugins.Count > 0) return Count(report.SksePlugins.Select(p => p.Name).ToList(), index.SkseDllNames);
        return null;
    }

    private static LoadoutFit Count(IReadOnlyList<string> logged, IReadOnlySet<string> inLoadout) =>
        new(logged.Count(name => !inLoadout.Contains(name)), logged.Count);
}
