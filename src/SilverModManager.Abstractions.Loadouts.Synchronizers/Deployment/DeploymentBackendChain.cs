using System.Collections.Immutable;
using NexusMods.Abstractions.Loadouts.Deployment;
using NexusMods.Sdk.Games;

namespace NexusMods.Abstractions.Loadouts.Synchronizers.Deployment;

/// <summary>
/// Picks the first backend in <see cref="Order"/> that is registered and whose preflight accepts the root.
/// </summary>
public sealed class DeploymentBackendChain(IEnumerable<IDeploymentBackend> backends) : IDeploymentBackendSelector
{
    /// <summary>
    /// The order backends are tried in. It is the only place the order lives, and a registered backend that
    /// is not listed here is never chosen, whatever its preflight says.
    /// </summary>
    public static readonly ImmutableArray<DeploymentBackendId> Order =
    [
        DeploymentBackendId.Reflink,
        DeploymentBackendId.Hardlink,
        DeploymentBackendId.Symlink,
        DeploymentBackendId.Copy,
    ];

    private readonly IReadOnlyDictionary<DeploymentBackendId, IDeploymentBackend> _backends = backends.ToDictionary(b => b.Id);

    /// <inheritdoc />
    public DeploymentChoice Choose(GameInstallation installation, GamePath root)
    {
        // Same locations ProbeWritableRoots skips: nothing is deployed there, so no probe file may land there either.
        if (IsFiltered(root))
            return new DeploymentChoice(DeploymentBackendId.Copy, "location is never written");

        var tried = new List<string>();
        foreach (var id in Order)
        {
            if (!_backends.TryGetValue(id, out var backend)) continue;

            var preflight = backend.Preflight(installation, root);
            tried.Add($"{id}: {preflight.Reason}");
            if (preflight.Accepted) return new DeploymentChoice(id, string.Join("; ", tried));
        }

        throw new InvalidOperationException($"No deployment backend accepts {root}: {string.Join("; ", tried)}");
    }

    // The saves folder is the one location every game's filter rejects whole; game-specific filters work on files.
    private static bool IsFiltered(GamePath root) => root.LocationId == LocationId.Saves;
}
