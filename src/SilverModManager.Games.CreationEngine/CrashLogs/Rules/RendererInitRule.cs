using SilverModManager.Games.CreationEngine.Abstractions;
using SilverModManager.Sdk;

namespace SilverModManager.Games.CreationEngine.CrashLogs.Rules;

/// <summary>
/// The game died inside <c>Renderer::Init</c> in its first half minute. The module list cannot say which DLL override
/// is wrong (<c>wined3d.dll</c> is loaded in healthy runs too), so the fix stops at "check the launch options".
/// </summary>
public sealed class RendererInitRule : ICrashCauseRule
{
    private static readonly TimeSpan StartupWindow = TimeSpan.FromSeconds(30);

    public CrashVerdict? Match(CrashReport report, CrashOwnership owners, ICreationEngineGame game)
    {
        if (report.Uptime is not { } uptime || uptime >= StartupWindow) return null;
        if (!report.Frames.Any(f => f.Kind == 'P' && f.Symbol is { } s && s.StartsWith("Renderer::Init", StringComparison.Ordinal))) return null;

        return new CrashVerdict(
            "The renderer failed to start",
            $"Check the DLL overrides in the Steam launch options ({WineParser.WineDllOverridesEnvironmentVariableName}) and the Proton version",
            null, true);
    }
}
