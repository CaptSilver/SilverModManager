using System.Diagnostics.CodeAnalysis;
using NexusMods.Paths;
using SilverModManager.Platform.Proton.ToolRunner.Dependencies;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Steam;

namespace SilverModManager.Platform.Proton.ToolRunner;

/// <summary>
/// Runs a program for a game in a native Heroic's prefix. A Proton runner goes through <c>umu-run</c> when
/// it is installed, and through the runner's <c>proton</c> script when it is not. A Wine runner is run
/// like any Wine prefix.
/// </summary>
public sealed class UmuStrategy(IProcessRunner processRunner, IEnvironmentReader environment) : IToolRunStrategy
{
    internal const string UmuStrategyName = "Umu";
    internal const string ProtonStrategyName = "Proton";

    private readonly UmuRunDependency _umu = new(processRunner);
    private readonly PlainWineStrategy _wine = new(processRunner);

    public CompatibilityLayerKind Kind => CompatibilityLayerKind.Heroic;

    public ToolRunVia? Via => ToolRunVia.Umu;

    public string? CheckRunnable(GameInstallation installation, ResolvedPrefix prefix)
    {
        if (!RunnerOf(installation, prefix, out var runner, out var diagnostic)) return diagnostic;
        if (!WineRunSupport.IsProtonScript(runner)) return _wine.CheckRunnable(installation, prefix);

        if (_umu.IsInstalled() || runner.FileExists) return null;
        return $"{_umu.MissingText} The Proton script at '{runner}' is not there either, so there is nothing to run the program with.";
    }

    public string? X86_64Requirement(GameInstallation installation, ResolvedPrefix prefix)
    {
        // CheckRunnable has already refused a prefix with no runner.
        if (!RunnerOf(installation, prefix, out var runner, out _)) return null;
        if (!WineRunSupport.IsProtonScript(runner)) return _wine.X86_64Requirement(installation, prefix);

        var game = installation.Game.DisplayName;
        var buildName = runner.Parent.FileName;
        if (_umu.IsInstalled())
        {
            // umu-run starts the proton script inside the Steam Linux Runtime its toolmanifest.vdf names; only
            // the steamrt4-arm64 runtime is an aarch64 program, every other one is x86-64 whatever Wine it holds.
            var runtime = VdfFile.Read(runner.Parent.Combine("toolmanifest.vdf"))?["require_tool_appid"]?.Value;
            if (runtime != Arm64RuntimeAppId)
                return $"umu-run would start the Proton runner {buildName} inside the x86-64 Steam Linux Runtime its toolmanifest.vdf asks for (require_tool_appid {runtime ?? "missing"}); pick a Proton runner built for ARM64 for {game} in Heroic";
        }

        return WineRunSupport.ProtonArm64Requirement(runner, buildName, environment, $"pick a Proton runner with ARM64 support for {game} in Heroic");
    }

    // Steam's app id for SteamLinuxRuntime_steamrt4-arm64, the one aarch64 runtime umu-launcher knows.
    private const string Arm64RuntimeAppId = "4185400";

    public ToolRunPlan Plan(
        GameInstallation installation,
        ResolvedPrefix prefix,
        PrefixCommand command)
    {
        if (!RunnerOf(installation, prefix, out var runner, out var diagnostic)) return ToolRunPlan.Failed(diagnostic);
        if (!WineRunSupport.IsProtonScript(runner)) return _wine.Plan(installation, prefix, command);

        var unrunnable = CheckRunnable(installation, prefix);
        if (unrunnable is not null) return ToolRunPlan.Failed(unrunnable);

        return _umu.IsInstalled()
            ? PlanUmu(installation, prefix, runner, command)
            : PlanProton(prefix, runner, command);
    }

    private static ToolRunPlan PlanUmu(
        GameInstallation installation,
        ResolvedPrefix prefix,
        AbsolutePath runner,
        PrefixCommand command)
    {
        var defaults = new Dictionary<string, string>
        {
            ["GAMEID"] = $"umu-{installation.LocatorResult.StoreIdentifier}",
            ["WINEPREFIX"] = prefix.Path.ToString(),
            ["PROTONPATH"] = runner.Parent.ToString(),
        };
        // umu-run hands the run to the same proton script.
        WineRunSupport.ProtonDefaults(defaults);
        var environment = WineRunSupport.Environment(defaults, command.Env);

        return ToolRunPlan.Runnable(UmuStrategyName, WineRunSupport.Argv(["umu-run"], command, prefix.Path), WineRunSupport.WorkingDirectory(command, prefix), environment);
    }

    private static ToolRunPlan PlanProton(
        ResolvedPrefix prefix,
        AbsolutePath runner,
        PrefixCommand command)
    {
        var compatData = ProtonCompatData.CompatDataOf(prefix.Path);
        var defaults = new Dictionary<string, string>
        {
            ["STEAM_COMPAT_DATA_PATH"] = compatData.ToString(),
        };
        WineRunSupport.ProtonDefaults(defaults);
        var environment = WineRunSupport.Environment(defaults, command.Env);

        return ToolRunPlan.Runnable(ProtonStrategyName, WineRunSupport.Argv([runner.ToString(), "run"], command, prefix.Path), WineRunSupport.WorkingDirectory(command, prefix), environment);
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
