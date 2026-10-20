using System.Diagnostics.CodeAnalysis;
using NexusMods.Paths;
using NexusMods.Platform.Proton.ToolRunner.Dependencies;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;

namespace NexusMods.Platform.Proton.ToolRunner;

/// <summary>
/// Runs a program for a game in a native Heroic's prefix. A Proton runner goes through <c>umu-run</c> when
/// it is installed, and through the runner's <c>proton</c> script when it is not. A Wine runner is run
/// like any Wine prefix.
/// </summary>
public sealed class UmuStrategy(IProcessRunner processRunner) : IToolRunStrategy
{
    internal const string UmuStrategyName = "Umu";
    internal const string ProtonStrategyName = "Proton";

    private readonly UmuRunDependency _umu = new(processRunner);
    private readonly PlainWineStrategy _wine = new(processRunner);

    public CompatibilityLayerKind Kind => CompatibilityLayerKind.Heroic;

    public string? CheckRunnable(GameInstallation installation, ResolvedPrefix prefix)
    {
        if (!RunnerOf(installation, prefix, out var runner, out var diagnostic)) return diagnostic;
        if (!WineRunSupport.IsProtonScript(runner)) return _wine.CheckRunnable(installation, prefix);

        if (_umu.IsInstalled() || runner.FileExists) return null;
        return $"{_umu.MissingText} The Proton script at '{runner}' is not there either, so there is nothing to run the program with.";
    }

    public ToolRunPlan Plan(
        GameInstallation installation,
        ResolvedPrefix prefix,
        AbsolutePath exe,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string>? env)
    {
        if (!RunnerOf(installation, prefix, out var runner, out var diagnostic)) return ToolRunPlan.Failed(diagnostic);
        if (!WineRunSupport.IsProtonScript(runner)) return _wine.Plan(installation, prefix, exe, args, env);

        var unrunnable = CheckRunnable(installation, prefix);
        if (unrunnable is not null) return ToolRunPlan.Failed(unrunnable);

        return _umu.IsInstalled()
            ? PlanUmu(installation, prefix, runner, exe, args, env)
            : PlanProton(prefix, runner, exe, args, env);
    }

    private static ToolRunPlan PlanUmu(
        GameInstallation installation,
        ResolvedPrefix prefix,
        AbsolutePath runner,
        AbsolutePath exe,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string>? env)
    {
        var environment = WineRunSupport.Environment(new Dictionary<string, string>
        {
            ["GAMEID"] = $"umu-{installation.LocatorResult.StoreIdentifier}",
            ["WINEPREFIX"] = prefix.Path.ToString(),
            ["PROTONPATH"] = runner.Parent.ToString(),
        }, env);

        return ToolRunPlan.Runnable(UmuStrategyName, WineRunSupport.Argv(["umu-run"], exe, args, prefix.Path), exe.Parent, environment);
    }

    private static ToolRunPlan PlanProton(
        ResolvedPrefix prefix,
        AbsolutePath runner,
        AbsolutePath exe,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string>? env)
    {
        // Proton wants the directory that holds pfx, and Heroic records either that directory or pfx itself.
        var compatData = prefix.Path.FileName == "pfx" ? prefix.Path.Parent : prefix.Path;
        var environment = WineRunSupport.Environment(new Dictionary<string, string>
        {
            ["STEAM_COMPAT_DATA_PATH"] = compatData.ToString(),
        }, env);

        return ToolRunPlan.Runnable(ProtonStrategyName, WineRunSupport.Argv([runner.ToString(), "run"], exe, args, prefix.Path), exe.Parent, environment);
    }

    private static bool RunnerOf(GameInstallation installation, ResolvedPrefix prefix, out AbsolutePath runner, [NotNullWhen(false)] out string? diagnostic)
    {
        runner = default;
        diagnostic = null;
        if (prefix.WineBinary is { } binary)
        {
            runner = binary;
            return true;
        }

        diagnostic = WineRunSupport.NoRunner(installation);
        return false;
    }
}
