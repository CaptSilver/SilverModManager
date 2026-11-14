using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.Deployment.Vfs;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Deployment.Fuse.Backend;

/// <summary>
/// A tree served over a root's folder, and what ends it: unmount, wait for the session loop, promote the game's writes.
/// </summary>
/// <remarks>
/// Promotion waits for the session loop to end because a process that still has a staged file open (a
/// <c>wineserver</c> outliving the game is the usual one) would keep writing to it while it is hashed. When the loop does
/// not end in time nothing is promoted: the staged files stay where they are and the next recovery takes them.
/// </remarks>
internal sealed class FuseMount(
    IFuseMount mount,
    VfsSession session,
    DirectoryHandle backing,
    GamePath root,
    TimeSpan completionTimeout,
    ILogger logger) : IDeploymentMount
{
    private bool _finished;

    /// <inheritdoc />
    public GamePath Root => root;

    /// <inheritdoc />
    public AbsolutePath MountPoint => mount.MountPoint;

    /// <inheritdoc />
    public async Task<MountFinishResult> Finish(CancellationToken token)
    {
        if (_finished) return new MountFinishResult(MountFinishStatus.NothingToDo, []);
        _finished = true;

        try
        {
            try
            {
                await mount.Unmount(token);
            }
            catch (IOException e)
            {
                // The mount stayed busy through its retries. Disposing detaches it lazily; the wait below then decides
                // whether anything still holds it.
                logger.LogWarning(e, "Unmounting {Root} did not finish; detaching it lazily", root);
            }
            await mount.DisposeAsync();

            var ended = await Task.WhenAny(mount.Completion, Task.Delay(completionTimeout, token));
            if (!ReferenceEquals(ended, mount.Completion))
            {
                token.ThrowIfCancellationRequested();
                logger.LogWarning("The session serving {Root} did not end within {Timeout}; staged files are left for the next recovery", root, completionTimeout);
                return new MountFinishResult(MountFinishStatus.StillInUse, []);
            }

            return Map(await session.FinishSession(token));
        }
        finally
        {
            backing.Dispose();
        }
    }

    private static MountFinishResult Map(PromotionResult result) => result.Status switch
    {
        PromotionStatus.Promoted or PromotionStatus.Recovered => new MountFinishResult(MountFinishStatus.Promoted, result.Files),
        PromotionStatus.Blocked => new MountFinishResult(MountFinishStatus.Blocked, result.Files, result.RecoveryPath),
        PromotionStatus.OverCap => new MountFinishResult(MountFinishStatus.OverCap, result.Files),
        _ => new MountFinishResult(MountFinishStatus.NothingToDo, result.Files),
    };

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_finished) return;
        await Finish(CancellationToken.None);
    }
}
