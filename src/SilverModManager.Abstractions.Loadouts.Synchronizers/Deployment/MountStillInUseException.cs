using SilverModManager.Sdk.Errors;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;

/// <summary>
/// A game or tool that has exited but left a process holding files open under a mounted folder. The changes it wrote
/// stay where they are and the next apply or launch takes them in, so the loadout is still applied.
/// </summary>
public sealed class MountStillInUseException(string message) : UserFacingException(message)
{
    /// <inheritdoc />
    public override string Title => "Some game files are still in use";

    /// <param name="what">The game or tool that was run, by its display name.</param>
    /// <param name="roots">The mounted roots it left busy, as <see cref="DeploymentRootNames.Describe"/> names them.</param>
    public static MountStillInUseException For(string what, IEnumerable<string> roots) =>
        new($"{what} still has files open under {string.Join(", ", roots)}; its changes are promoted the next time the app applies or launches");
}
