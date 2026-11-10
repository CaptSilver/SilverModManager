using SilverModManager.Games.CreationEngine.Abstractions;

namespace SilverModManager.Games.CreationEngine.CrashLogs.Rules;

/// <summary>
/// The log names missing masters and no plugin was loaded, so the game stopped loading plugins. Health Check's own
/// Missing Master entry says which plugin and what to do; this only points at it.
/// </summary>
public sealed class MissingMastersRule : ICrashCauseRule
{
    public CrashVerdict? Match(CrashReport report, CrashOwnership owners, ICreationEngineGame game)
    {
        if (report.MissingMasters.Count == 0 || report.PluginCount != 0) return null;

        return new CrashVerdict(
            "The game stopped loading plugins because a master is missing",
            "See the Missing Master entry in this Health Check",
            null, true);
    }
}
