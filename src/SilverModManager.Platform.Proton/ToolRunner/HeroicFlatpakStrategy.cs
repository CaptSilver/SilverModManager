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
        => CheckRunner(installation, prefix) ?? (_heroic.IsInstalled() ? null : _heroic.MissingText);

    public ToolRunPlan Plan(
        GameInstallation installation,
        ResolvedPrefix prefix,
        AbsolutePath exe,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string>? env)
    {
        var diagnostic = CheckRunnable(installation, prefix);
        if (diagnostic is not null) return ToolRunPlan.Failed(diagnostic);

        // Every variable goes as --env rather than on the flatpak process: flatpak resets some host variables
        // on the way into the sandbox, and --env also wins over anything the app's manifest sets.
        var environment = WineRunSupport.Environment(new Dictionary<string, string> { ["WINEPREFIX"] = prefix.Path.ToString() }, env);

        List<string> head = ["flatpak", "run", $"--command={prefix.WineBinary!.Value}"];
        head.AddRange(environment.Select(kv => $"--env={kv.Key}={kv.Value}"));
        head.Add(HeroicFlatpakDependency.FlatpakId);

        return ToolRunPlan.Runnable(StrategyName, WineRunSupport.Argv(head, exe, args, prefix.Path), exe.Parent, new Dictionary<string, string>());
    }

    private static string? CheckRunner(GameInstallation installation, ResolvedPrefix prefix)
    {
        if (prefix.WineBinary is not { } runner) return WineRunSupport.NoRunner(installation);

        return WineRunSupport.IsProtonScript(runner)
            ? $"{installation.Game.DisplayName} uses the Proton runner '{runner}' in Heroic's Flatpak, and running programs in a Proton runner inside the Flatpak is not supported."
            : null;
    }
}
