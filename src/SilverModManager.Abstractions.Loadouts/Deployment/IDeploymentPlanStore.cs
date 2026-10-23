using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Abstractions.Loadouts.Deployment;

/// <summary>
/// Reads and records which backend deploys each root of a game installation.
/// </summary>
public interface IDeploymentPlanStore
{
    /// <summary>
    /// The recorded plan. An installation without rows yields <see cref="DeploymentPlan.Empty"/>, which
    /// routes every path to the copy backend.
    /// </summary>
    DeploymentPlan Load(IDb db, GameInstallMetadataId installation);

    /// <summary>
    /// Adds an entry to <paramref name="tx"/> for every location root the recorded plan misses, with the
    /// backend <paramref name="selector"/> picks for it, and returns the completed plan. Roots already
    /// recorded are left alone.
    /// </summary>
    DeploymentPlan EnsureComplete(ITransaction tx, IDb db, GameInstallation installation, GameInstallMetadataId id, IDeploymentBackendSelector selector);

    /// <summary>
    /// Records <paramref name="backend"/> for <paramref name="root"/>, replacing the backend of an
    /// existing entry for that root. <paramref name="db"/> is where the existing entry is looked up, so
    /// a write after <see cref="EnsureComplete"/> in the same transaction finds nothing the transaction added:
    /// pass the db that <see cref="EnsureComplete"/> read.
    /// </summary>
    /// <param name="chosen">
    /// True when a person picked the backend. False clears a mark the row already carries, so the
    /// selection chain may move it again.
    /// </param>
    void Write(ITransaction tx, IDb db, GameInstallMetadataId installation, GamePath root, DeploymentBackendId backend, bool chosen);
}
