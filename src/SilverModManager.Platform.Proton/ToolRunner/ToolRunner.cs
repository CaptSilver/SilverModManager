using System.Diagnostics.CodeAnalysis;
using System.Text;
using CliWrap;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using NexusMods.Paths;
using SilverModManager.Platform.Proton.Launch;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.Platform.Proton.ToolRunner;

/// <summary>
/// Picks the strategy for the kind of prefix the game runs in and runs the program through it.
/// </summary>
public sealed class ToolRunner(
    IPrefixResolver prefixResolver,
    IEnumerable<IToolRunStrategy> strategies,
    IProcessRunner processRunner,
    IGameRegistry gameRegistry,
    DeploymentLaunchRoots launchRoots,
    HostExecution hostExecution,
    ISettingsManager settingsManager,
    ILogger<ToolRunner>? logger = null) : IToolRunner
{
    private readonly IToolRunStrategy[] _strategies = DistinctByKind(strategies.Where(s => s is not ILastResortToolRunStrategy));
    private readonly ILastResortToolRunStrategy[] _lastResorts = DistinctByKind(strategies.OfType<ILastResortToolRunStrategy>()).Cast<ILastResortToolRunStrategy>().ToArray();

    // A second registration for a kind would otherwise be shadowed by whichever was registered first.
    private static IToolRunStrategy[] DistinctByKind(IEnumerable<IToolRunStrategy> strategies)
    {
        var array = strategies.ToArray();
        var duplicate = array.GroupBy(s => s.Kind).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new ArgumentException($"More than one tool run strategy is registered for {duplicate.Key} prefixes.", nameof(strategies));

        return array;
    }

    /// <inheritdoc/>
    public ToolRunPlan Plan(
        GameInstallation installation,
        AbsolutePath exe,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string>? env,
        ToolRunVia via = ToolRunVia.Auto)
    {
        if (!TryGetStrategy(installation, via, out var strategy, out var prefix, out var diagnostic, out var cannotRunX86_64))
            return ToolRunPlan.Failed(diagnostic, cannotRunX86_64);

        return WithLaunchRoots(installation, strategy.Plan(installation, prefix, PrefixCommand.ForExe(exe, args, env)));
    }

    /// <summary>
    /// Builds the plan for running <paramref name="command"/> in the game's prefix, or the reason there is none.
    /// </summary>
    public ToolRunPlan PlanInPrefix(GameInstallation installation, PrefixCommand command)
    {
        if (!TryGetStrategy(installation, ToolRunVia.Auto, out var strategy, out var prefix, out var diagnostic, out var cannotRunX86_64))
            return ToolRunPlan.Failed(diagnostic, cannotRunX86_64);

        return WithLaunchRoots(installation, strategy.Plan(installation, prefix, command));
    }

    private ToolRunPlan WithLaunchRoots(GameInstallation installation, ToolRunPlan plan)
    {
        if (!plan.IsRunnable) return plan;

        // Symlinks written by the deployment backends point into a folder the Steam container does not share
        // unless it is listed, and a tool run through Proton starts that container like the game does. A game
        // with no recorded metadata isn't managed, so nothing was deployed for it.
        if (!gameRegistry.TryGetMetadata(installation, out var metadata)) return plan;

        var roots = launchRoots.For(installation, metadata.Id);
        if (roots.Count == 0) return plan;

        var environment = new Dictionary<string, string>(plan.Environment);
        PressureVesselFilesystems.Merge(environment, roots);
        return ToolRunPlan.Runnable(plan.Strategy, plan.Argv, plan.WorkingDirectory.Value, environment);
    }

    /// <inheritdoc/>
    public async Task<ToolRunResult> RunWindowsTool(
        GameInstallation installation,
        AbsolutePath exe,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string>? env,
        bool logOutput,
        CancellationToken cancellationToken,
        ToolRunVia via = ToolRunVia.Auto)
    {
        var plan = Plan(installation, exe, args, env, via);
        if (!plan.IsRunnable) throw Refusal(plan.Diagnostic, plan.CannotRunX86_64);

        var result = await processRunner.RunAsync(ToCommand(plan), logOutput, cancellationToken);
        return new ToolRunResult(result.ExitCode);
    }

    /// <inheritdoc/>
    public async Task<PrefixRunResult> RunInPrefix(GameInstallation installation, PrefixCommand command, CancellationToken cancellationToken)
    {
        var plan = PlanInPrefix(installation, command);
        if (!plan.IsRunnable) throw Refusal(plan.Diagnostic, plan.CannotRunX86_64);

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var cli = ToCommand(plan)
            .WithStandardOutputPipe(PipeTarget.ToStringBuilder(stdout))
            .WithStandardErrorPipe(PipeTarget.ToStringBuilder(stderr));

        // The output comes back to the caller, so it is not also written to the process log.
        var result = await processRunner.RunAsync(cli, logOutput: false, cancellationToken);
        return new PrefixRunResult(result.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private static Exception Refusal(string diagnostic, bool cannotRunX86_64)
        => cannotRunX86_64 ? new X86_64HostRequiredException(diagnostic) : new InvalidOperationException(diagnostic);

    // Only called for a runnable plan, which always has a working directory.
    private static Command ToCommand(ToolRunPlan plan) => Cli.Wrap(plan.Argv[0])
        .WithArguments(plan.Argv.Skip(1))
        .WithWorkingDirectory((plan.WorkingDirectory ?? throw new InvalidOperationException(plan.Diagnostic)).ToString())
        .WithEnvironmentVariables(plan.Environment.ToDictionary(kv => kv.Key, kv => (string?)kv.Value))
        .WithValidation(CommandResultValidation.None);

    /// <inheritdoc/>
    public bool CanRun(GameInstallation installation, [NotNullWhen(false)] out string? diagnostic)
        => CanRun(installation, out diagnostic, out _);

    /// <inheritdoc/>
    public bool CanRun(GameInstallation installation, [NotNullWhen(false)] out string? diagnostic, out bool cannotRunX86_64)
        => TryGetStrategy(installation, ToolRunVia.Auto, out _, out _, out diagnostic, out cannotRunX86_64);

    private bool TryGetStrategy(
        GameInstallation installation,
        ToolRunVia via,
        [NotNullWhen(true)] out IToolRunStrategy? strategy,
        [NotNullWhen(true)] out ResolvedPrefix? prefix,
        [NotNullWhen(false)] out string? diagnostic,
        out bool cannotRunX86_64)
    {
        strategy = null;
        prefix = null;
        cannotRunX86_64 = false;

        var resolution = prefixResolver.Resolve(installation);
        if (resolution.IsNative)
        {
            diagnostic = $"{installation.Game.DisplayName} runs natively, so there is no Windows program to run for it.";
            return false;
        }

        if (!resolution.IsResolved)
        {
            diagnostic = resolution.Diagnostic ?? $"No Wine prefix was found for {installation.Game.DisplayName}.";
            return false;
        }

        if (via is not ToolRunVia.Auto) return TryGetForcedStrategy(installation, via, resolution.Prefix, out strategy, out prefix, out diagnostic, out cannotRunX86_64);

        var kind = resolution.Prefix.Kind;
        strategy = _strategies.FirstOrDefault(s => s.Kind == kind);
        if (strategy is null)
        {
            diagnostic = $"{installation.Game.DisplayName} runs in a {kind} prefix, and running programs in those has no strategy yet.";
            return false;
        }

        var resolved = resolution.Prefix;
        diagnostic = strategy.CheckRunnable(installation, resolved);
        if (diagnostic is not null)
        {
            // The regular strategy's reason is the one worth showing: it is the way the program is meant to run.
            var lastResort = _lastResorts.FirstOrDefault(s => s.Kind == kind && s.CheckRunnable(installation, resolved) is null);
            if (lastResort is null)
            {
                strategy = null;
                return false;
            }

            strategy = lastResort;
            diagnostic = null;
        }

        // Gated on the strategy that would actually run, so a last resort is refused the same way.
        if (X86_64Refusal(installation, strategy) is { } refusal)
        {
            strategy = null;
            diagnostic = refusal;
            cannotRunX86_64 = true;
            return false;
        }

        prefix = resolved;
        return true;
    }

    // A forced strategy is the user's explicit choice, so when it cannot run the reason is reported as it is;
    // quietly running something else would defeat the setting.
    private bool TryGetForcedStrategy(
        GameInstallation installation,
        ToolRunVia via,
        ResolvedPrefix resolved,
        [NotNullWhen(true)] out IToolRunStrategy? strategy,
        [NotNullWhen(true)] out ResolvedPrefix? prefix,
        [NotNullWhen(false)] out string? diagnostic,
        out bool cannotRunX86_64)
    {
        strategy = null;
        prefix = null;
        cannotRunX86_64 = false;

        if (via is ToolRunVia.Native)
        {
            diagnostic = "A native program is started directly, not through a Wine prefix.";
            return false;
        }

        var forced = _strategies.FirstOrDefault(s => s.Via == via);
        if (forced is null)
        {
            diagnostic = $"Nothing is registered to run programs via {via}.";
            return false;
        }

        if (X86_64Refusal(installation, forced) is { } refusal)
        {
            diagnostic = refusal;
            cannotRunX86_64 = true;
            return false;
        }

        diagnostic = forced.CheckRunnable(installation, resolved);
        if (diagnostic is not null) return false;

        strategy = forced;
        prefix = resolved;
        return true;
    }

    /// <summary>
    /// The one place every program start passes through to learn whether this machine can start the
    /// strategy's x86-64 launcher. Null when it can, when the strategy is not x86-64, or when the user said
    /// to assume it can.
    /// </summary>
    private string? X86_64Refusal(GameInstallation installation, IToolRunStrategy strategy)
    {
        if (!strategy.NeedsX86_64Host) return null;

        var support = hostExecution.CanRunX86_64();
        if (support is X86_64Support.Native or X86_64Support.Interpreter) return null;

        if (settingsManager.Get<HostExecutionSettings>().AssumeX86_64Runs)
        {
            logger?.LogTrace("Running {Game} through {Strategy} although no x86-64 support was found ({Support}), because {Setting} is on",
                installation.Game.DisplayName, strategy.Kind, support, OverrideKey);
            return null;
        }

        var game = installation.Game.DisplayName;
        var fixHint = $"If Proton does run here, turn on {OverrideKey} with: settings set -k {OverrideKey} -v true";
        return support is X86_64Support.Unknown
            ? $"{game}'s Windows programs run through {strategy.Kind}, an x86-64 program, and this machine is not x86-64 and could not read {hostExecution.BinfmtDirectory} to check for an x86-64 interpreter (FEX). {fixHint}"
            : $"{game}'s Windows programs run through {strategy.Kind}, an x86-64 program, and this machine is not x86-64 and has no x86-64 binfmt interpreter (FEX) registered. {fixHint}";
    }

    internal const string OverrideKey = "HostExecutionSettings.AssumeX86_64Runs";
}
