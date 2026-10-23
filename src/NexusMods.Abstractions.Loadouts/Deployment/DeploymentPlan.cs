using System.Collections.Immutable;
using NexusMods.Paths;
using NexusMods.Sdk.Games;

namespace NexusMods.Abstractions.Loadouts.Deployment;

/// <summary>
/// Sends everything under <paramref name="Root"/> to <paramref name="Backend"/>.
/// </summary>
/// <param name="Chosen">Whether a person picked the backend, as opposed to the selection chain.</param>
/// <param name="Reason">The chain's explanation for its pick, when it made one.</param>
public readonly record struct DeploymentPlanRoot(GamePath Root, DeploymentBackendId Backend, bool Chosen = false, string? Reason = null);

/// <summary>
/// Which backend deploys which part of a game installation. A path belongs to the root with the
/// longest matching prefix in its location.
/// </summary>
public sealed record DeploymentPlan(ImmutableArray<DeploymentPlanRoot> Roots)
{
    public static readonly DeploymentPlan Empty = new(ImmutableArray<DeploymentPlanRoot>.Empty);

    /// <summary>
    /// The backend for <paramref name="path"/>: the root whose path is the longest whole-segment prefix
    /// of it. Falls back to <see cref="DeploymentBackendId.Copy"/> when no root covers the location, which
    /// <see cref="IsComplete"/> reports as a gap.
    /// </summary>
    public DeploymentBackendId BackendFor(GamePath path)
    {
        var best = DeploymentBackendId.Copy;
        var bestLength = -1;
        foreach (var entry in Roots)
        {
            if (entry.Root.LocationId != path.LocationId) continue;

            var length = entry.Root.Path.Path.Length;
            if (length <= bestLength || !IsPrefix(entry.Root.Path, path.Path)) continue;

            best = entry.Backend;
            bestLength = length;
        }
        return best;
    }

    /// <summary>
    /// True when some root is served through a mount, which only exists while the app is running the game.
    /// </summary>
    public bool UsesMount => Roots.Any(r => r.Backend == DeploymentBackendId.Fuse);

    /// <summary>
    /// True when every location has a root for its whole tree.
    /// </summary>
    public bool IsComplete(GameLocations locations) => !MissingRoots(locations).Any();

    /// <summary>
    /// The location roots in <paramref name="locations"/> that no plan root covers.
    /// </summary>
    public IEnumerable<GamePath> MissingRoots(GameLocations locations)
    {
        foreach (var locationId in locations.Keys)
        {
            var covered = Roots.Any(r => r.Root.LocationId == locationId && r.Root.Path == RelativePath.Empty);
            if (!covered) yield return new GamePath(locationId, RelativePath.Empty);
        }
    }

    /// <summary>
    /// Routes every location, nested ones included, to the copy backend.
    /// </summary>
    public static DeploymentPlan Default(GameLocations locations)
    {
        var roots = ImmutableArray.CreateBuilder<DeploymentPlanRoot>(locations.Count);
        foreach (var locationId in locations.Keys)
        {
            roots.Add(new DeploymentPlanRoot(new GamePath(locationId, RelativePath.Empty), DeploymentBackendId.Copy));
        }
        return new DeploymentPlan(roots.MoveToImmutable());
    }

    // InFolder compares whole segments ("mods" must not claim "modsX/a.txt") and ignores case like
    // GamePath equality does, so a sync node never lands on a different backend than its folder.
    private static bool IsPrefix(RelativePath prefix, RelativePath path) =>
        prefix == RelativePath.Empty || path == prefix || path.InFolder(prefix);
}
