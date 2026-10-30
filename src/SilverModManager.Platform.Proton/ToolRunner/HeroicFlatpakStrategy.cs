using NexusMods.Paths;
using SilverModManager.Platform.Proton.ToolRunner.Dependencies;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Platform.Proton.ToolRunner;

/// <summary>
/// Runs a program inside the Flatpak sandbox of Heroic, with the runner Heroic picked for the game, so the
/// sandbox's own libraries and permissions are the ones the game runs with.
/// </summary>
/// <remarks>
/// Only Wine runners are handled. Running a Proton runner from the sandbox needs the Steam runtime
/// container nested inside it, which is not something to guess at.
/// </remarks>
public sealed class HeroicFlatpakStrategy(IProcessRunner processRunner) : IToolRunStrategy
{
    internal const string StrategyName = "HeroicFlatpak";

    private readonly HeroicFlatpakDependency _heroic = new(processRunner);

    public CompatibilityLayerKind Kind => CompatibilityLayerKind.HeroicFlatpak;

    public ToolRunVia? Via => ToolRunVia.HeroicFlatpak;

    public string? CheckRunnable(GameInstallation installation, ResolvedPrefix prefix)
        => CheckRunnable(installation, prefix, out _);

    // The narrowed runner comes out of the check so Plan does not repeat the null test.
    private string? CheckRunnable(GameInstallation installation, ResolvedPrefix prefix, out AbsolutePath wine)
    {
        var diagnostic = CheckRunner(installation, prefix, out wine);
        return diagnostic ?? (_heroic.IsInstalled() ? null : _heroic.MissingText);
    }

    public ToolRunPlan Plan(
        GameInstallation installation,
        ResolvedPrefix prefix,
        PrefixCommand command)
    {
        var diagnostic = CheckRunnable(installation, prefix, out var wine);
        if (diagnostic is not null) return ToolRunPlan.Failed(diagnostic);

        // Every variable goes as --env rather than on the flatpak process: flatpak resets some host variables
        // on the way into the sandbox, and --env also wins over anything the app's manifest sets.
        var environment = WineRunSupport.Environment(new Dictionary<string, string> { ["WINEPREFIX"] = prefix.Path.ToString() }, command.Env);

        List<string> head = ["flatpak", "run", $"--command={wine}"];
        head.AddRange(environment.Select(kv => $"--env={kv.Key}={kv.Value}"));
        head.Add(HeroicFlatpakDependency.FlatpakId);

        return ToolRunPlan.Runnable(StrategyName, WineRunSupport.Argv(head, command, prefix.Path), WineRunSupport.WorkingDirectory(command, prefix), new Dictionary<string, string>());
    }

    private static string? CheckRunner(GameInstallation installation, ResolvedPrefix prefix, out AbsolutePath runner)
    {
        runner = default;
        if (prefix.WineBinary is not { } wine) return WineRunSupport.NoRunner(installation);

        runner = wine;
        return WineRunSupport.IsProtonScript(runner)
            ? $"{installation.Game.DisplayName} uses the Proton runner '{runner}' in Heroic's Flatpak, and running programs in a Proton runner inside the Flatpak is not supported."
            : null;
    }
}
