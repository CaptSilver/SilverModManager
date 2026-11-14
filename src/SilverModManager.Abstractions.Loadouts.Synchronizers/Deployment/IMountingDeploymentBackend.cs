using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;

/// <summary>
/// A backend whose files exist only while something holds a mount over the root.
/// </summary>
public interface IMountingDeploymentBackend : IDeploymentBackend
{
    /// <summary>
    /// Mounts the loadout's view over the root. Runs after the apply that recorded it. Throws on failure.
    /// </summary>
    Task<IDeploymentMount> Mount(GameInstallation installation, Loadout.ReadOnly loadout, GamePath root, CancellationToken token);

    /// <summary>
    /// What a mount left behind: a dead session's mount at the root, an unfinished promotion, staged files.
    /// Idempotent; safe when nothing is there. Refuses (<see cref="MountRecoveryStatus.StillRunning"/>) while the
    /// process tracker lists game processes.
    /// </summary>
    Task<MountRecoveryReport> Recover(GameInstallation installation, GamePath root, LoadoutId? target, CancellationToken token);

    /// <summary>
    /// Whether the kernel lists a mount of ours at the root right now.
    /// </summary>
    bool IsMounted(GameInstallation installation, GamePath root);

    /// <summary>
    /// The rows recorded for the root, for the disk side of the sync tree.
    /// </summary>
    MountManifestView Manifest(GameInstallMetadataId installation, GamePath root, IDb db);
}

/// <summary>
/// A mount held over a root.
/// </summary>
public interface IDeploymentMount : IAsyncDisposable
{
    GamePath Root { get; }

    AbsolutePath MountPoint { get; }

    /// <summary>
    /// Unmounts, waits (bounded) for the session loop to end, then promotes staged writes into the target loadout.
    /// <see cref="MountFinishStatus.StillInUse"/> when the wait timed out: nothing is promoted and the next recovery does it.
    /// </summary>
    Task<MountFinishResult> Finish(CancellationToken token);
}

public enum MountFinishStatus { Promoted, NothingToDo, StillInUse, Blocked, OverCap }

/// <param name="Status">How the finish ended.</param>
/// <param name="Files">The files promoted into the loadout.</param>
/// <param name="Recovery">Where unpromoted files were kept, when they were.</param>
public sealed record MountFinishResult(MountFinishStatus Status, IReadOnlyList<RelativePath> Files, AbsolutePath? Recovery = null);

/// <summary>
/// What a recovery did. <see cref="OverCap"/>: the staged files add up to more than the promotion size cap, so all of
/// them are still in <see cref="MountRecoveryReport.Staging"/>.
/// </summary>
public enum MountRecoveryStatus { Nothing, Detached, Promoted, StillRunning, NeedsTarget, Blocked, OverCap }

public sealed record MountRecoveryReport(MountRecoveryStatus Status, bool WasMounted, AbsolutePath? Staging = null, AbsolutePath? Recovery = null, int PromotedFiles = 0);
