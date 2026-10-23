using NexusMods.Paths;
using NexusMods.Platform.Proton.ToolRunner;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;

namespace NexusMods.Platform.Proton.Protontricks;

/// <summary>
/// Runs a program for a Steam game through <c>protontricks-launch</c>, which finds the game's Proton build
/// itself. Only used when no Proton build resolves for the game, since it cannot be given the environment
/// Steam would.
/// </summary>
public sealed class ProtontricksStrategy(AggregateProtontricksDependency protontricks) : ILastResortToolRunStrategy
{
    internal const string StrategyName = "Protontricks";

    public CompatibilityLayerKind Kind => CompatibilityLayerKind.SteamProton;

    public string? CheckRunnable(GameInstallation installation, ResolvedPrefix prefix)
        => Resolve(installation, prefix).Diagnostic;

    public ToolRunPlan Plan(
        GameInstallation installation,
        ResolvedPrefix prefix,
        AbsolutePath exe,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string>? env)
    {
        var (dependency, appId, diagnostic) = Resolve(installation, prefix);
        if (diagnostic is not null) return ToolRunPlan.Failed(diagnostic);

        var translated = args.Select(arg => WindowsPathTranslator.Translate(arg, prefix.Path.ToString())).ToList();
        var argv = dependency!.MakeLaunchArgv(appId, exe, translated);
        return ToolRunPlan.Runnable(StrategyName, argv, exe.Parent, env ?? new Dictionary<string, string>());
    }

    private (IProtontricksDependency? Dependency, long AppId, string? Diagnostic) Resolve(GameInstallation installation, ResolvedPrefix prefix)
    {
        var gameName = installation.Game.DisplayName;

        if (installation.LocatorResult.Store != GameStore.Steam || prefix.ProtonAppId is not { } appId)
            return (null, 0, $"{gameName} is not a Steam game with a known app id, so protontricks has no app to launch the program in.");

        // The probe runs on the thread pool so that blocking on it from a thread with a synchronization context,
        // such as the UI thread, cannot deadlock on its continuations.
        var dependency = Task.Run(() => protontricks.FindAvailable().AsTask()).GetAwaiter().GetResult();
        if (dependency is null)
            return (null, 0, $"Neither protontricks nor its Flatpak is installed. See {protontricks.Homepage}.");

        return (dependency, appId, null);
    }
}
