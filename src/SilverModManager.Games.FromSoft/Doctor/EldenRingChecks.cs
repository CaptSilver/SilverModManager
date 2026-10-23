using NexusMods.Games.FromSoft.EldenRing;
using NexusMods.Games.FromSoft.Me3;
using NexusMods.Games.FromSoft.Overlay;
using NexusMods.Paths;

namespace NexusMods.Games.FromSoft.Doctor;

/// <summary>
/// <see cref="FromSoftChecks"/> with Elden Ring's rules filled in, for code that only ever deals with Elden Ring.
/// </summary>
public static class EldenRingChecks
{
    /// <summary>The artifacts, spawner and anti-cheat checks of one <c>Game</c> folder.</summary>
    public static IReadOnlyList<CheckResult> GameFolder(AbsolutePath gameFolder)
        => FromSoftChecks.GameFolder(gameFolder, EldenRingGame.Definition.CheckRules);

    /// <inheritdoc cref="FromSoftChecks.OverlaySupport"/>
    public static CheckResult OverlaySupport(IExecutableLocator executables, ISandboxProbe probe)
        => FromSoftChecks.OverlaySupport(executables, probe);

    /// <inheritdoc cref="Me3Loader.HostCheckResult"/>
    public static CheckResult Me3(AbsolutePath binary, bool executable, string? version)
        => Me3Loader.HostCheckResult(binary, executable, version);

    /// <inheritdoc cref="FromSoftChecks.Upperdir"/>
    public static CheckResult? Upperdir(IReadOnlyList<AuditFinding> pending) => FromSoftChecks.Upperdir(pending);
}
