using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;

/// <summary>
/// How a deployment root is written in messages and on the command line.
/// </summary>
public static class DeploymentRootNames
{
    /// <summary>A root as <c>Location</c> or <c>Location/path</c>.</summary>
    public static string Describe(GamePath root) => root.Path == RelativePath.Empty
        ? root.LocationId.ToString()
        : $"{root.LocationId}/{root.Path}";
}
