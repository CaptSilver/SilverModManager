using NexusMods.Paths;
using SilverModManager.Platform.Proton.ToolRunner;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Platform.Proton.Protontricks;

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
        PrefixCommand command)
    {
        // protontricks-launch takes a file to run, so a bare verb has nothing to be looked up by.
        if (command.Exe is not { } exe)
            return ToolRunPlan.Failed($"protontricks-launch can only run an executable, so '{command.Program}' cannot be run through protontricks.");

        var (dependency, appId, diagnostic) = Resolve(installation, prefix);
        if (diagnostic is not null) return ToolRunPlan.Failed(diagnostic);

        var argv = dependency!.MakeLaunchArgv(appId, exe, WineRunSupport.Arguments(command, prefix.Path).ToList());
        return ToolRunPlan.Runnable(StrategyName, argv, exe.Parent, command.Env ?? new Dictionary<string, string>());
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
