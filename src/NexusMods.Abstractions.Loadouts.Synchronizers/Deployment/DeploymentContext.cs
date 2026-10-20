using NexusMods.MnemonicDB.Abstractions;
using NexusMods.MnemonicDB.Abstractions.TxFunctions;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Abstractions.Loadouts.Synchronizers.Deployment;

/// <summary>
/// State shared by every backend and phase of one synchronize run.
/// </summary>
public sealed class DeploymentContext
{
    public required ALoadoutSynchronizer Synchronizer { get; init; }

    public required GameInstallation Installation { get; init; }

    public required GameInstallMetadataId MetadataId { get; init; }

    public GameLocations Locations => Installation.Locations;

    /// <summary>
    /// The whole sync tree, not just the nodes of one backend: intrinsic files read every node.
    /// </summary>
    public required Dictionary<GamePath, SyncNode> SyncTree { get; init; }

    /// <summary>
    /// Null when resetting a game without a loadout.
    /// </summary>
    public Loadout.ReadOnly? Loadout { get; init; }

    public required ITransaction Transaction { get; init; }

    public SynchronizeLoadoutJob? Job { get; init; }

    /// <summary>
    /// Created on first use and then reused by every backend and phase of the run.
    /// </summary>
    public EntityId? OverridesGroup { get; set; }
}
