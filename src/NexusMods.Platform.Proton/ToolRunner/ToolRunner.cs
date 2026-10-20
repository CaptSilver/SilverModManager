using System.Diagnostics.CodeAnalysis;
using CliWrap;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;

namespace NexusMods.Platform.Proton.ToolRunner;

/// <summary>
/// Picks the strategy for the kind of prefix the game runs in and runs the program through it.
/// </summary>
public sealed class ToolRunner(
    IPrefixResolver prefixResolver,
    IEnumerable<IToolRunStrategy> strategies,
    IProcessRunner processRunner) : IToolRunner
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
        IReadOnlyDictionary<string, string>? env)
    {
        if (!TryGetStrategy(installation, out var strategy, out var prefix, out var diagnostic))
            return ToolRunPlan.Failed(diagnostic);

        return strategy.Plan(installation, prefix, exe, args, env);
    }

    /// <inheritdoc/>
    public async Task<ToolRunResult> RunWindowsTool(
        GameInstallation installation,
        AbsolutePath exe,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string>? env,
        bool logOutput,
        CancellationToken cancellationToken)
    {
        var plan = Plan(installation, exe, args, env);
        if (!plan.IsRunnable) throw new InvalidOperationException(plan.Diagnostic);

        var command = Cli.Wrap(plan.Argv[0])
            .WithArguments(plan.Argv.Skip(1))
            .WithWorkingDirectory(plan.WorkingDirectory.Value.ToString())
            .WithEnvironmentVariables(plan.Environment.ToDictionary(kv => kv.Key, kv => (string?)kv.Value))
            .WithValidation(CommandResultValidation.None);

        var result = await processRunner.RunAsync(command, logOutput, cancellationToken);
        return new ToolRunResult(result.ExitCode);
    }

    /// <inheritdoc/>
    public bool CanRun(GameInstallation installation, [NotNullWhen(false)] out string? diagnostic)
    {
        return TryGetStrategy(installation, out _, out _, out diagnostic);
    }

    private bool TryGetStrategy(
        GameInstallation installation,
        [NotNullWhen(true)] out IToolRunStrategy? strategy,
        [NotNullWhen(true)] out ResolvedPrefix? prefix,
        [NotNullWhen(false)] out string? diagnostic)
    {
        strategy = null;
        prefix = null;

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

        var kind = resolution.Prefix.Kind;
        strategy = _strategies.FirstOrDefault(s => s.Kind == kind);
        if (strategy is null)
        {
            diagnostic = $"{installation.Game.DisplayName} runs in a {kind} prefix, and running programs in those has no strategy yet.";
            return false;
        }

        var resolved = resolution.Prefix;
        prefix = resolved;
        diagnostic = strategy.CheckRunnable(installation, resolved);
        if (diagnostic is null) return true;

        // The regular strategy's reason is the one worth showing: it is the way the program is meant to run.
        var lastResort = _lastResorts.FirstOrDefault(s => s.Kind == kind && s.CheckRunnable(installation, resolved) is null);
        if (lastResort is null) return false;

        strategy = lastResort;
        diagnostic = null;
        return true;
    }
}
