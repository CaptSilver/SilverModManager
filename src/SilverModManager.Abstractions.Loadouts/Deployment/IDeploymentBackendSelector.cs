using SilverModManager.Sdk.Games;

namespace SilverModManager.Abstractions.Loadouts.Deployment;

/// <summary>
/// The backend picked for a root and why, in words fit for the doctor report.
/// </summary>
public readonly record struct DeploymentChoice(DeploymentBackendId Backend, string Reason);

/// <summary>
/// Picks the backend for a root of a game installation that has no recorded row.
/// </summary>
/// <remarks>
/// Lives beside <see cref="DeploymentPlan"/> because the plan store calls it and cannot reference the
/// project that holds the backends.
/// </remarks>
public interface IDeploymentBackendSelector
{
    /// <summary>
    /// May create and delete probe files under the root, so it must not run while the game is.
    /// </summary>
    DeploymentChoice Choose(GameInstallation installation, GamePath root);
}
