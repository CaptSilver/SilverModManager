using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;

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
    /// The modification time the game wants on each file, by path. A backend sets it when it writes the file, so
    /// the time it records for the file is the final one; files this does not name keep the time they get.
    /// </summary>
    public IReadOnlyDictionary<GamePath, DateTimeOffset> PlannedModifiedTimes { get; init; } = new Dictionary<GamePath, DateTimeOffset>();

    /// <summary>
    /// Created on first use and then reused by every backend and phase of the run.
    /// </summary>
    public EntityId? OverridesGroup { get; set; }
}
