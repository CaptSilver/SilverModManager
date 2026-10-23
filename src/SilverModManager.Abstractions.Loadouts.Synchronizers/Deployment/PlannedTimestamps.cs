using SilverModManager.Abstractions.Loadouts.Synchronizers.Rules;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.IO;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;

/// <summary>
/// Sets the modification times a timestamp-ordered game asked for (<see cref="ITimestampOrderedGame"/>) and keeps
/// the disk state in step, shared by every backend.
/// </summary>
internal static class PlannedTimestamps
{
    /// <summary>
    /// Sets the planned time on a file that was just written, before the backend reads the time to record. A path
    /// the plan does not name is left alone. On a symlink this sets the time of the file it leads to, which is
    /// also the time <see cref="FileFingerprint.Of"/> reports for the link.
    /// </summary>
    internal static void ApplyTo(DeploymentContext context, GamePath path, AbsolutePath file)
    {
        if (context.PlannedModifiedTimes.TryGetValue(path, out var planned))
            File.SetLastWriteTimeUtc(file.ToString(), planned.UtcDateTime);
    }

    /// <summary>
    /// Sets the time on files that were already on disk and writes it to their disk state entry in the apply's
    /// transaction. A file the apply extracts is skipped, it got its time from <see cref="ApplyTo"/>; so is one
    /// the apply ingests, whose disk state entry the ingest already writes in this transaction. That file gets
    /// its time on the next apply.
    /// </summary>
    internal static void Stamp(DeploymentContext context, IReadOnlyList<(GamePath Path, DateTimeOffset Mtime)> stamps)
    {
        foreach (var (path, mtime) in stamps)
        {
            if (!context.SyncTree.TryGetValue(path, out var node)) continue;
            if (!node.HaveDisk || node.Actions.HasFlag(Actions.ExtractToDisk) || node.Actions.HasFlag(Actions.IngestFromDisk)) continue;

            var file = context.Locations.ToAbsolutePath(path);
            if (FileFingerprint.Of(file).LastModified.UtcTicks != mtime.UtcTicks)
                File.SetLastWriteTimeUtc(file.ToString(), mtime.UtcDateTime);

            var written = FileFingerprint.Of(file).LastModified;
            if (written.UtcTicks != node.Disk.LastModifiedTicks)
                context.Transaction.Add(node.Disk.EntityId, DiskStateEntry.LastModified, written);
        }
    }
}
