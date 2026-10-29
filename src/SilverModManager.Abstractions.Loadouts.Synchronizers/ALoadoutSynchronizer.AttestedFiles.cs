using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Rules;
using SilverModManager.Abstractions.Games.FileHashes;
using SilverModManager.Abstractions.Games.FileHashes.Models;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.IO;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers;

public partial class ALoadoutSynchronizer
{
    /// <summary>
    /// One whole-file read made for the synchronizer's own use, with the fingerprint the file kept through it.
    /// </summary>
    private readonly record struct WholeRead(GamePath Path, MultiHash Hashes, FileFingerprint Fingerprint);

    /// <summary>
    /// Gives every node the backends are about to archive or ingest an xxHash3 disk identity, which the file store and
    /// <see cref="LoadoutFile"/> are keyed by. A disk part the index took from the store's manifest carries the
    /// manifest's SHA-1, so those files are read here, once, and the attestation they came from is settled before any
    /// backend runs: if the settle fails the apply stops with nothing archived, deleted or extracted.
    /// </summary>
    /// <remarks>
    /// Reads every node with <see cref="Actions.BackupFile"/> or <see cref="Actions.IngestFromDisk"/> whose disk
    /// identity is not an xxHash3, and every game-folder <see cref="Actions.BackupFile"/> node with no
    /// <see cref="HashRelation"/> for its bytes: those are about to be streamed into the store anyway, and recording
    /// their digests lets a later baseline resolve the path and lets reset find the backup through its SHA-1.
    /// A node whose identity changed has its signature and actions worked out again, because they were chosen for the
    /// old one. When the SHA-1 matched, <see cref="SyncNode.Previous"/> follows: the last sync saw these bytes, it
    /// named them by SHA-1. When it did not match, the last sync did not see the bytes now on disk and
    /// <c>Previous</c> keeps its SHA-1.
    /// </remarks>
    public async Task ResolveDiskIdentitiesForWrites(
        Dictionary<GamePath, SyncNode> tree,
        GameInstallation installation,
        ITransaction tx,
        BackupOverflowChoice choice = BackupOverflowChoice.BackUpAnyway,
        CancellationToken cancellationToken = default,
        SynchronizeLoadoutJob? job = null)
    {
        var wanted = NodesNeedingBytes(tree);
        var reads = wanted.Count == 0 ? [] : await ReadAndSettle(wanted, installation, cancellationToken, ProgressOfReading(tree, wanted, job));

        var resolved = new Dictionary<GamePath, SyncNode>();
        foreach (var read in reads)
        {
            ref var node = ref CollectionsMarshal.GetValueRefOrNullRef(tree, read.Path);
            var before = node.Disk.Identity;
            var xxHash3 = FileIdentity.XxHash3(read.Hashes.XxHash3);
            if (before == xxHash3) continue;

            var matched = before.Kind == HashKind.Sha1 && read.Hashes.Sha1 == Sha1Value.From(before.Digest) && read.Hashes.Size == node.Disk.Size;
            node.Disk = node.Disk with { Identity = xxHash3 };
            if (matched && node.HavePrevious && node.Previous.Identity == before)
                node.Previous = node.Previous with { Identity = xxHash3 };
            resolved[read.Path] = node;
        }

        await NameArchivedGameFilesByTheirArchiveKey(tree, resolved);
        if (resolved.Count == 0) return;

        // The signatures were built for the old identities, so the actions are worked out again. An adopt answer
        // was applied to the old actions and has to be applied to the new ones too.
        foreach (var path in resolved.Keys.ToArray())
        {
            var node = resolved[path];
            MapActions(path, ref node);
            resolved[path] = node;
        }

        AdjustActions(resolved);
        ApplyBackupOverflowChoice(resolved, choice);

        foreach (var (path, node) in resolved)
        {
            tree[path] = node;

            // A deleted entry is retracted and an overwritten one is rewritten by the backend in this same
            // transaction, so only an entry that survives the apply is updated here.
            if (!node.HaveDisk || !node.Disk.HaveEntityId) continue;
            if (node.Actions.HasFlag(Actions.DeleteFromDisk) || node.Actions.HasFlag(Actions.ExtractToDisk)) continue;
            tx.Add(node.Disk.EntityId, DiskStateEntry.Identity, node.Disk.Identity);
            tx.Add(node.Disk.EntityId, DiskStateEntry.XxHash3, node.Disk.Identity.XxHash3());
        }
    }

    /// <summary>
    /// A game file the store vouched for is named by its SHA-1, and when a mod covered it the app archived the bytes
    /// under their xxHash3. A node that is about to be extracted from that archive is given the archive's key, found
    /// through the relation recorded when it was made, because every backend reads a loadout part as an xxHash3.
    /// </summary>
    private async Task NameArchivedGameFilesByTheirArchiveKey(Dictionary<GamePath, SyncNode> tree, Dictionary<GamePath, SyncNode> resolved)
    {
        foreach (var (path, node) in tree.ToArray())
        {
            if (!node.Actions.HasFlag(Actions.ExtractToDisk) || !node.HaveLoadout || node.Loadout.Identity.Kind != HashKind.Sha1) continue;
            if (await ArchiveKeyOfSha1Async(node.Loadout.Identity, node.Loadout.Size) is not { } key) continue;

            var named = node with { Loadout = node.Loadout with { Identity = FileIdentity.XxHash3(key) } };
            tree[path] = named;
            resolved[path] = named;
        }
    }

    private WholeReadProgress ProgressOfReading(Dictionary<GamePath, SyncNode> tree, List<GamePath> wanted, SynchronizeLoadoutJob? job)
        => new(job, Logger, wanted.ToDictionary(path => path, path => (long)tree[path].Disk.Size.Value));

    private List<GamePath> NodesNeedingBytes(Dictionary<GamePath, SyncNode> tree)
    {
        var wanted = new List<GamePath>();
        foreach (var (path, node) in tree)
        {
            if (!node.HaveDisk) continue;
            var backsUp = node.Actions.HasFlag(Actions.BackupFile);
            if (!backsUp && !node.Actions.HasFlag(Actions.IngestFromDisk)) continue;

            if (!node.Disk.Identity.TryXxHash3(out var xxHash3)) wanted.Add(path);
            else if (backsUp && path.LocationId == LocationId.Game && !_fileHashService.TryFindRelation(xxHash3, out _)) wanted.Add(path);
        }

        return wanted;
    }

    /// <summary>
    /// Reads the files, then records what the reads found before anything else happens: an attested file's row is
    /// settled, and the digests of any other file are remembered as a relation and a fingerprint row.
    /// </summary>
    private async Task<List<WholeRead>> ReadAndSettle(List<GamePath> paths, GameInstallation installation, CancellationToken cancellationToken, WholeReadProgress? progress = null)
    {
        var reads = await ReadWholeFiles(paths, installation, cancellationToken, progress);
        var attested = _fileHashService.AttestedFiles(installation);

        var settle = new List<(AttestedLookup Attested, MultiHash Hashes, FileFingerprint After)>();
        var remember = new List<IndexedFileHash>();
        foreach (var read in reads)
        {
            if (attested.TryGetValue(read.Path, out var row)) settle.Add((row, read.Hashes, read.Fingerprint));
            else remember.Add(new IndexedFileHash(read.Path, read.Fingerprint.Size, read.Fingerprint.LastModified, read.Hashes.XxHash3, read.Hashes));
        }

        await _fileHashService.SettleAttested(installation, settle, cancellationToken);
        if (remember.Count > 0) await _fileHashService.RecordIndexedFiles(installation, remember, cancellationToken);
        return reads;
    }

    /// <summary>
    /// Reads each file once with every digest, taking its fingerprint before and after.
    /// </summary>
    private async Task<List<WholeRead>> ReadWholeFiles(List<GamePath> paths, GameInstallation installation, CancellationToken cancellationToken, WholeReadProgress? progress)
    {
        progress?.Begin();
        var results = new ConcurrentBag<WholeRead>();
        var options = new ParallelOptions { MaxDegreeOfParallelism = HashingLimits.FileReaders, CancellationToken = cancellationToken };
        await Parallel.ForEachAsync(paths, options, async (path, token) =>
        {
            var (hashes, fingerprint) = await StableFileRead.Read(_fullHasher, installation.Locations.ToAbsolutePath(path), token);
            results.Add(new WholeRead(path, hashes, fingerprint));
            progress?.FileRead(path);
        });

        return results.ToList();
    }
}
