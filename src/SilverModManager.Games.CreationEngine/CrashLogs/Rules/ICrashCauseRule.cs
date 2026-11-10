using SilverModManager.Games.CreationEngine.Abstractions;

namespace SilverModManager.Games.CreationEngine.CrashLogs.Rules;

/// <summary>A known cause of a crash. Returns null when the report does not show it.</summary>
public interface ICrashCauseRule
{
    CrashVerdict? Match(CrashReport report, CrashOwnership owners, ICreationEngineGame game);
}
