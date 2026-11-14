using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.Deployment.Fuse.Mount;
using SilverModManager.Deployment.Vfs;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Deployment.Fuse.Backend;

/// <summary>
/// Cleans up after a session that died with its mount up: detaches the mount, finishes an interrupted promotion, and
/// promotes what the game staged.
/// </summary>
/// <remarks>
/// Liveness decides, not the mount table: a game process that is still around means the mount is in use whatever the
/// kernel lists. The steps run in an order that makes a second call a no-op, so a crash halfway through is finished by
/// the next recovery.
/// </remarks>
internal sealed class MountRecovery(IProcessTracker tracker, IFuseUnmounter unmounter, ILogger logger)
{
    public async Task<MountRecoveryReport> Run(
        GameInstallation installation, AbsolutePath mountPoint, bool mounted,
        StagingDirectory staging, StagingPromoter promoter, LoadoutId? target, CancellationToken token)
    {
        if (tracker.FindGameProcesses(installation).Count > 0) return new MountRecoveryReport(MountRecoveryStatus.StillRunning, WasMounted: false);

        // Lazy: with no process alive nothing holds a file open, and a dead daemon leaves the mount refusing a plain unmount.
        if (mounted)
        {
            var result = await unmounter.Unmount(mountPoint, lazy: true, token);
            if (result.Status == UnmountStatus.Failed) throw MountDetachFailedException.For(mountPoint.ToString(), result.StdErr.Trim());
            logger.LogInformation("Detached the stale mount at {MountPoint}", mountPoint);
        }

        if (staging.JournalFile.FileExists)
            return Map(await promoter.Recover(token), mounted, staging);

        if (!HasStagedChanges(staging)) return new MountRecoveryReport(mounted ? MountRecoveryStatus.Detached : MountRecoveryStatus.Nothing, mounted);

        // The loadout the files belong to is not guessed: the one last synced may not be the one the dead run served.
        if (target is not { } loadout) return new MountRecoveryReport(MountRecoveryStatus.NeedsTarget, mounted, Staging: staging.Root);

        return Map(await promoter.Promote(loadout, token), mounted, staging);
    }

    private static bool HasStagedChanges(StagingDirectory staging) =>
        staging.Scan().Any(static e => !e.IsDirectory) || staging.ReadWhiteouts().Count > 0;

    private static MountRecoveryReport Map(PromotionResult result, bool mounted, StagingDirectory staging) => result.Status switch
    {
        PromotionStatus.Promoted or PromotionStatus.Recovered => new MountRecoveryReport(MountRecoveryStatus.Promoted, mounted, PromotedFiles: result.Files.Count),
        PromotionStatus.Blocked => new MountRecoveryReport(MountRecoveryStatus.Blocked, mounted, Recovery: result.RecoveryPath),
        PromotionStatus.OverCap => new MountRecoveryReport(MountRecoveryStatus.OverCap, mounted, Staging: staging.Root),
        _ => new MountRecoveryReport(mounted ? MountRecoveryStatus.Detached : MountRecoveryStatus.Nothing, mounted),
    };
}
