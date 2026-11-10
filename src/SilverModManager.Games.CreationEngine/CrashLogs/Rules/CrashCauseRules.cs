using SilverModManager.Games.CreationEngine.Abstractions;

namespace SilverModManager.Games.CreationEngine.CrashLogs.Rules;

public static class CrashCauseRules
{
    public static readonly IReadOnlyList<ICrashCauseRule> Default = [new EnbLightMeshRule(), new RendererInitRule(), new MissingMastersRule()];

    /// <summary>The verdict of the first rule in <see cref="Default"/> that matches, or null.</summary>
    public static CrashVerdict? FirstMatch(CrashReport report, CrashOwnership owners, ICreationEngineGame game) =>
        Default.Select(rule => rule.Match(report, owners, game)).FirstOrDefault(v => v is not null);
}
