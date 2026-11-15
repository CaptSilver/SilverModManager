using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using CommunityToolkit.HighPerformance.Buffers;
using DynamicData.Kernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SilverModManager.Abstractions.Games.FileHashes;
using SilverModManager.Abstractions.Games.FileHashes.Models;
using SilverModManager.Abstractions.GC;
using SilverModManager.Abstractions.Loadouts.Deployment;
using SilverModManager.Abstractions.Loadouts.Exceptions;
using SilverModManager.Abstractions.Loadouts.Files.Diff;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.Sdk.GeneratedCaches;
using SilverModManager.Abstractions.Loadouts.Sorting;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Rules;
using NexusMods.Hashing.xxHash3;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.IndexSegments;
using SilverModManager.MnemonicDB.Abstractions.Query;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using NexusMods.Paths;
using SilverModManager.Sdk;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.ForeignDeployments;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Loadouts;
using Reloaded.Memory.Extensions;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers;

/// <summary>
/// What an apply of a loadout would change right now: the status's "is there work" and the diff view's tree.
/// Reads only; it builds and processes a sync tree through the synchronizer and never writes. It logs through the
/// synchronizer's own loggers so the log categories stay the synchronizer's.
/// </summary>
internal sealed class PendingChanges(ALoadoutSynchronizer synchronizer, ILogger logger, ILogger perFileLogger)
{
    /// <summary>
    /// Whether applying the loadout would change anything, and whether a file is waiting on an archive that is not
    /// there.
    /// </summary>
    public async ValueTask<SynchronizeEvaluation> Evaluate(Loadout.ReadOnly loadout, IEnumerable<PathPartPair> previousDiskState, IEnumerable<PathPartPair> lastScannedDiskState)
    {
        var syncTree = synchronizer.BuildSyncTree(lastScannedDiskState, previousDiskState, loadout);
        // Process the sync tree to get the actions populated in the nodes
        synchronizer.ProcessSyncTree(syncTree);
        var waitsForArchive = syncTree.Values.Any(node => node.Actions.HasFlag(Actions.WarnOfUnableToExtract));

        // A file with no archive to extract it from is only a warning to an apply, so it is not work until the
        // archive is back and the same node maps to an extraction.
        var pending = syncTree.Where(n => n.Value.Actions != Actions.DoNothing && n.Value.Actions != Actions.WarnOfUnableToExtract).ToList();
        TracePending(loadout, pending);

        if (pending.Count == 0) return new SynchronizeEvaluation(false, waitsForArchive);
        if (!pending.All(n => IsIntrinsicWriteOfAnUntouchedFile(n.Value))) return new SynchronizeEvaluation(true, waitsForArchive);

        var files = synchronizer.IntrinsicFiles(loadout);
        foreach (var (path, node) in pending)
        {
            if (!files.TryGetValue(path, out var file) || file is not IIntrinsicFileRenderedFromLoadout) return new SynchronizeEvaluation(true, waitsForArchive);
            if (!await RendersAsRecorded(file, loadout, syncTree, node)) return new SynchronizeEvaluation(true, waitsForArchive);
        }

        return new SynchronizeEvaluation(false, waitsForArchive);
    }

    // One line per pass, because the status looks again on every change and a game folder holds thousands of files.
    // The file-by-file listing is on the per-file category.
    private void TracePending(Loadout.ReadOnly loadout, List<KeyValuePair<GamePath, SyncNode>> pending)
    {
        if (pending.Count == 0) return;

        if (logger.IsEnabled(LogLevel.Trace))
        {
            var byAction = ActionPhrases.Summarize(pending.Select(n => n.Value.Actions));
            var sample = pending.Take(PendingSampleSize).Select(n => n.Key.ToString());
            logger.LogTrace("Pending in loadout {LoadoutId}: {Count} file(s) ({ByAction}); first {SampleSize}: {Sample}. Every file is listed by logger {PerFileCategory} when {PerFileVariable}=1 is set before the app starts",
                loadout.LoadoutId, pending.Count, string.Join(", ", byAction), PendingSampleSize, string.Join(", ", sample), SynchronizerLogging.PerFileCategory, SynchronizerLogging.PerFileEnvironmentVariable);
        }

        if (!perFileLogger.IsEnabled(LogLevel.Trace)) return;
        foreach (var (path, node) in pending)
            perFileLogger.LogTrace("Pending in loadout {LoadoutId}: {Path} ({Actions})", loadout.LoadoutId, path, ActionPhrases.Describe(node.Actions));
    }

    private const int PendingSampleSize = 3;

    /// <summary>
    /// A generated file that is on disk as the app left it, with the recorded identity of what the app wrote: the
    /// only kind of node whose write can be skipped by comparing a render with that identity.
    /// </summary>
    private static bool IsIntrinsicWriteOfAnUntouchedFile(SyncNode node) =>
        node.Actions == Actions.WriteIntrinsic
        && node.SourceItemType == LoadoutSourceItemType.Intrinsic
        && node is { HaveDisk: true, HavePrevious: true }
        && node.Previous.Identity.Kind == HashKind.XxHash3
        && node.Disk.Identity.MatchSameKind(node.Previous.Identity) == IdentityMatch.Same;

    private async ValueTask<bool> RendersAsRecorded(IIntrinsicFile file, Loadout.ReadOnly loadout, Dictionary<GamePath, SyncNode> syncTree, SyncNode node)
    {
        try
        {
            using var rendered = new MemoryStream();
            await file.Write(rendered, loadout, syncTree);
            rendered.Position = 0;
            var hash = await Xx3Hasher.HashAsync(rendered);
            return FileIdentity.XxHash3(hash).MatchSameKind(node.Previous.Identity) == IdentityMatch.Same;
        }
        catch (Exception e)
        {
            // The apply will hit the same failure and report it properly; the status only needs to say there is work.
            logger.LogDebug(e, "{Path} could not be rendered to compare it with what was written, so it counts as pending", file.Path);
            return false;
        }
    }

    /// <summary>
    /// The files an apply would add, change or remove, as the preview shows them.
    /// </summary>
    public FileDiffTree DiskDiff(Loadout.ReadOnly loadout, List<PathPartPair> previousDiskState, List<PathPartPair> lastScannedDiskState)
    {
        var syncTree = synchronizer.BuildSyncTree(lastScannedDiskState, previousDiskState, loadout);
        // Process the sync tree to get the actions populated in the nodes
        synchronizer.ProcessSyncTree(syncTree);

        List<KeyValuePair<GamePath, DiskDiffEntry>> diffs = [];

        foreach (var (path, node) in syncTree)
        {
            var syncNode = node;
            var actions = syncNode.Actions;
            DiskDiffEntry entry;

            if (actions.HasFlag(Actions.DoNothing))
            {
                entry = new DiskDiffEntry
                {
                    Identity = node.Loadout.Identity,
                    Size = node.Loadout.Size,
                    ChangeType = FileChangeType.None,
                    GamePath = path,
                };
                diffs.Add(KeyValuePair.Create(path, entry));
            }
            else if (actions.HasFlag(Actions.WarnOfUnableToExtract))
            {
                // Nothing can be written for it, so the preview does not offer it as an addition.
                entry = new DiskDiffEntry
                {
                    Identity = node.Loadout.Identity,
                    Size = node.Loadout.Size,
                    ChangeType = FileChangeType.Unrestorable,
                    GamePath = path,
                };
            }
            else if (actions.HasFlag(Actions.ExtractToDisk))
            {
                entry = new DiskDiffEntry
                {
                    Identity = node.Loadout.Identity,
                    Size = node.Loadout.Size,
                    // If paired with a delete action, this is a modified file not a new one
                    ChangeType = actions.HasFlag(Actions.DeleteFromDisk) ? FileChangeType.Modified : FileChangeType.Added,
                    GamePath = path,
                };
            }
            else if (actions.HasFlag(Actions.DeleteFromDisk))
            {
                entry = new DiskDiffEntry
                {
                    Identity = node.Disk.Identity,
                    Size = node.Disk.Size,
                    ChangeType = FileChangeType.Removed,
                    GamePath = path,
                };
            }
            else if (actions.HasFlag(Actions.IngestFromDisk))
            {
                // File is already on disk and will not be changed
                entry = new DiskDiffEntry
                {
                    Identity = node.Disk.Identity,
                    Size = node.Disk.Size,
                    ChangeType = FileChangeType.None,
                    GamePath = path,
                };
            }
            else if (actions.HasFlag(Actions.AddReifiedDelete))
            {
                // File is not on disk and will not end up on disk, so don't show it
                continue;
            }
            else
            {
                // This really should become some sort of error state
                entry = new DiskDiffEntry
                {
                    Identity = FileIdentity.None,
                    Size = Size.Zero,
                    ChangeType = FileChangeType.None,
                    GamePath = path,
                };
            }

            diffs.Add(KeyValuePair.Create(path, entry));
        }

        return FileDiffTree.Create(diffs);
    }
}
