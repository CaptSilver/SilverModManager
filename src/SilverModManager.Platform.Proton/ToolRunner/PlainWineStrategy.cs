using System.Collections.Concurrent;
using NexusMods.Paths;
using SilverModManager.Platform.Proton.ToolRunner.Dependencies;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Platform.Proton.ToolRunner;

/// <summary>
/// Runs a program with a <c>wine</c> executable and <c>WINEPREFIX</c>: the binary the locator recorded for the
/// prefix, or the one on PATH when it recorded none.
/// </summary>
public sealed class PlainWineStrategy(IProcessRunner processRunner) : IToolRunStrategy
{
    internal const string StrategyName = "PlainWine";
    private const string WineOnPath = "wine";

    private readonly ConcurrentDictionary<string, WineDependency> _dependencies = new();

    public CompatibilityLayerKind Kind => CompatibilityLayerKind.PlainWine;

    public ToolRunVia? Via => ToolRunVia.Wine;

    public string? CheckRunnable(GameInstallation installation, ResolvedPrefix prefix)
    {
        var dependency = DependencyFor(Binary(prefix));
        return dependency.IsInstalled() ? null : dependency.MissingText;
    }

    public ToolRunPlan Plan(
        GameInstallation installation,
        ResolvedPrefix prefix,
        PrefixCommand command)
    {
        var binary = Binary(prefix);
        var diagnostic = CheckRunnable(installation, prefix);
        if (diagnostic is not null) return ToolRunPlan.Failed(diagnostic);

        var environment = WineRunSupport.Environment(new Dictionary<string, string> { ["WINEPREFIX"] = prefix.Path.ToString() }, command.Env);
        return ToolRunPlan.Runnable(StrategyName, WineRunSupport.Argv([binary], command, prefix.Path), WineRunSupport.WorkingDirectory(command, prefix), environment);
    }

    private static string Binary(ResolvedPrefix prefix) => prefix.WineBinary?.ToString() ?? WineOnPath;

    private WineDependency DependencyFor(string binary)
        => _dependencies.GetOrAdd(binary, b => new WineDependency(processRunner, b));
}
