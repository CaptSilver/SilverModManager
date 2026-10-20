using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk.Games;

namespace NexusMods.Abstractions.Loadouts.Deployment;

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
    /// Adds a copy entry to <paramref name="tx"/> for every location root the recorded plan misses and
    /// returns the completed plan. Roots already recorded are left alone.
    /// </summary>
    DeploymentPlan EnsureComplete(ITransaction tx, IDb db, GameInstallation installation, GameInstallMetadataId id);

    /// <summary>
    /// Records <paramref name="backend"/> for <paramref name="root"/>, replacing the backend of an
    /// existing entry for that root.
    /// </summary>
    void Write(ITransaction tx, GameInstallMetadataId installation, GamePath root, DeploymentBackendId backend);
}
