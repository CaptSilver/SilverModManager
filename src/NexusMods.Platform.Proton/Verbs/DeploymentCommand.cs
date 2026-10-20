using System.Text.Json;
using NexusMods.Abstractions.Cli;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Deployment;
using NexusMods.Abstractions.Loadouts.Exceptions;
using NexusMods.Abstractions.Loadouts.Synchronizers.Deployment;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Platform.Proton.Doctor;
using NexusMods.Platform.Proton.Launch;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.ProxyConsole;

namespace NexusMods.Platform.Proton.Verbs;

/// <summary>
/// What happened to one root.
/// </summary>
public enum DeploymentOutcome
{
    /// <summary>The root was reset with its old backend and deployed again with the new one.</summary>
    Moved,

    /// <summary>A person's pick named the backend the root already had, and it was already marked as chosen.</summary>
    Unchanged,

    /// <summary>A person's pick named the backend the root already had, which is now marked as chosen.</summary>
    Pinned,

    /// <summary>The selection chain picks the backend the root already has.</summary>
    MatchesChain,

    /// <summary>A person chose the backend, so the chain's pick was not applied.</summary>
    LeftChosen,
}

/// <param name="Root">The root as <c>Location</c> or <c>Location/path</c>.</param>
/// <param name="Before">The backend the root was deployed with.</param>
/// <param name="After">The backend it is deployed with now.</param>
/// <param name="Outcome">What was done.</param>
/// <param name="Reason">The selection chain's explanation, when it picked the backend.</param>
public sealed record DeploymentRootResult(string Root, string Before, string After, DeploymentOutcome Outcome, string? Reason);

/// <summary>
/// What <c>silvermod deployment</c> did to a game, and the plan afterwards.
/// </summary>
public sealed record DeploymentResult(string Game, IReadOnlyList<DeploymentRootResult> Roots, IReadOnlyList<DoctorDeploymentRoot> Deployment);

/// <summary>
/// Moves the roots of a game to another deployment backend: to one a person names, or to what the selection chain
/// picks now. Installs recorded before the chain existed read as copy and only move through this.
/// </summary>
public sealed class DeploymentCommand(
    ISynchronizerService synchronizer,
    LoadoutResolver resolver,
    IGameRegistry registry,
    IConnection connection,
    IDeploymentPlanStore plans,
    IDeploymentBackendSelector selector,
    IEnumerable<IDeploymentBackend> backends,
    IGameLaunchSession launchSession,
    ILooseStagingStore store)
{
    private static readonly JsonSerializerOptions JsonOptions = DoctorReport.JsonOptions;

    public async Task<int> RunAsync(IRenderer renderer, string game, string? root, string? use, bool recommended, bool json)
    {
        if ((use is null) == !recommended) return await Fail(renderer, "Give exactly one of --use <backend> or --recommended.");

        var installation = resolver.ResolveGame(game, out var gameError);
        if (installation is null) return await Fail(renderer, gameError!);
        if (!registry.TryGetMetadata(installation, out var metadata)) return await Fail(renderer, $"{installation.Game.DisplayName} is not managed.");

        var registered = backends.Select(b => b.Id).ToHashSet();
        DeploymentBackendId? wanted = null;
        if (use is not null)
        {
            var match = registered.Where(id => string.Equals(id.Value, use.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
            if (match.Length == 1) wanted = match[0];
            else
            {
                return await Fail(renderer, $"No deployment backend is called '{use}'. Registered: {string.Join(", ", registered.Select(id => id.Value).Order(StringComparer.Ordinal))}.");
            }
        }

        var name = installation.Game.DisplayName;
        // Asking the chain writes probe files into the game folders, so a running game is refused up front.
        if (launchSession.IsRunning(installation)) return await Fail(renderer, RunningMessage(name));

        var plan = plans.Load(connection.Db, metadata.GameInstallMetadataId);
        var known = plan.Roots.Select(r => r.Root).Concat(plan.MissingRoots(installation.Locations)).Distinct().ToArray();
        var targets = root is null ? known : known.Where(r => string.Equals(DoctorDeploymentRoot.Describe(r), root.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        if (targets.Length == 0)
        {
            return await Fail(renderer, $"{name} has no root '{root}'. Roots: {string.Join(", ", known.Select(DoctorDeploymentRoot.Describe))}.");
        }

        var results = new List<DeploymentRootResult>();
        try
        {
            foreach (var (target, choice) in wanted is null ? Choose(installation, plan, targets) : targets.Select(t => (t, (DeploymentChoice?)null)))
            {
                results.Add(await Apply(installation, plan, target, wanted, choice));
            }
        }
        catch (ExecutableInUseException e)
        {
            return await Fail(renderer, e.Message);
        }

        var after = DoctorDeploymentRoot.From(plans.Load(connection.Db, metadata.GameInstallMetadataId), installation.Locations, registered);
        if (json)
        {
            await renderer.TextLine(JsonSerializer.Serialize(new DeploymentResult(name, results, after), JsonOptions));
            return 0;
        }

        foreach (var result in results) await renderer.TextLine(Describe(result));
        await renderer.TextLine("Deployment:");
        foreach (var line in after) await renderer.TextLine($"  {DoctorTextFormatter.DeploymentRoot(line)}");
        return 0;
    }

    // The probes write into the store directory, which a sweep must not meet half way.
    private IReadOnlyList<(GamePath Root, DeploymentChoice? Choice)> Choose(GameInstallation installation, DeploymentPlan plan, IEnumerable<GamePath> targets)
    {
        using var _ = store.Hold();
        return targets
            .Select(t => (t, (DeploymentChoice?)(IsChosen(plan, t) ? null : selector.Choose(installation, t))))
            .ToArray();
    }

    private async Task<DeploymentRootResult> Apply(GameInstallation installation, DeploymentPlan plan, GamePath target, DeploymentBackendId? wanted, DeploymentChoice? choice)
    {
        var before = plan.BackendFor(target);
        var name = DoctorDeploymentRoot.Describe(target);

        if (wanted is { } picked)
        {
            var changed = await synchronizer.ChangeDeploymentBackend(installation, target, picked, chosen: true);
            var outcome = changed ? DeploymentOutcome.Moved : IsChosen(plan, target) ? DeploymentOutcome.Unchanged : DeploymentOutcome.Pinned;
            return new DeploymentRootResult(name, before.Value, picked.Value, outcome, null);
        }

        if (choice is not { } chain) return new DeploymentRootResult(name, before.Value, before.Value, DeploymentOutcome.LeftChosen, null);
        if (chain.Backend == before) return new DeploymentRootResult(name, before.Value, before.Value, DeploymentOutcome.MatchesChain, chain.Reason);

        await synchronizer.ChangeDeploymentBackend(installation, target, chain.Backend, chosen: false);
        return new DeploymentRootResult(name, before.Value, chain.Backend.Value, DeploymentOutcome.Moved, chain.Reason);
    }

    private static bool IsChosen(DeploymentPlan plan, GamePath root) => plan.Roots.Any(r => r.Root == root && r.Chosen);

    private static string Describe(DeploymentRootResult result)
    {
        var reason = result.Reason is null ? "" : $" ({result.Reason})";
        return result.Outcome switch
        {
            DeploymentOutcome.Moved => $"{result.Root}: {result.Before} -> {result.After}{reason}",
            DeploymentOutcome.Unchanged => $"{result.Root}: {result.After}, already chosen",
            DeploymentOutcome.Pinned => $"{result.Root}: {result.After}, now chosen",
            DeploymentOutcome.MatchesChain => $"{result.Root}: {result.After}, already what the chain picks{reason}",
            _ => $"{result.Root}: {result.After}, chosen, left as is",
        };
    }

    private static string RunningMessage(string game)
        => $"{game} is running from SilverModManager. Quit the game before changing how it is deployed.";

    private static async Task<int> Fail(IRenderer renderer, string message)
    {
        await renderer.TextLine(message);
        return LaunchSession.FailureExitCode;
    }
}
