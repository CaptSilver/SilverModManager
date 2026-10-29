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
    /// old one. A <see cref="SyncNode.Previous"/> or loadout part that is the SHA-1 of the bytes read follows the
    /// disk part to their xxHash3; one that is another file's SHA-1 keeps it.
    /// </remarks>
    public async Task ResolveDiskIdentitiesForWrites(
        Dictionary<GamePath, SyncNode> tree,
        GameInstallation installation,
        ITransaction tx,
        BackupOverflowChoice choice = BackupOverflowChoice.BackUpAnyway,
        CancellationToken cancellationToken = default,
        SynchronizeLoadoutJob? job = null)
    {
        // Nodes the earlier pass already read are xxHash3 by now; this covers a caller that comes straight here.
        await ResolveUnresolvedIdentities(tree, installation, cancellationToken, job);

        var wanted = NodesNeedingBytes(tree);
        var reads = wanted.Count == 0 ? [] : await ReadAndSettle(wanted, installation, cancellationToken, ProgressOfReading(tree, wanted, job));

        var resolved = new Dictionary<GamePath, SyncNode>();
        foreach (var read in reads)
        {
            ref var node = ref CollectionsMarshal.GetValueRefOrNullRef(tree, read.Path);
            if (node.Disk.Identity.MatchSameKind(FileIdentity.XxHash3(read.Hashes.XxHash3)) == IdentityMatch.Same) continue;

            NameByTheBytesRead(ref node, read);
            resolved[read.Path] = node;
        }

        // Files an earlier read named: their actions are worked out, but the disk state entry still has the old name.
        foreach (var (path, named) in tree)
        {
            if (named.DiskNamedByRead) resolved[path] = named;
        }

        await NameArchivedGameFilesByTheirArchiveKey(tree, resolved);
        if (resolved.Count == 0) return;

        // The signatures were built for the old identities, so the actions are worked out again. An adopt answer
        // was applied to the old actions and has to be applied to the new ones too.
        var resolver = _identityResolver.Scoped();
        foreach (var path in resolved.Keys.ToArray())
        {
            var node = resolved[path];
            MapActions(path, ref node, resolver);
            resolved[path] = node;
        }

        AdjustActions(resolved);
        ApplyBackupOverflowChoice(resolved, choice);

        foreach (var (path, entry) in resolved)
        {
            var node = entry;
            node.DiskNamedByRead = false;
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
    /// Reads, once, every node whose present parts are named by kinds no recorded digest joins, so the file is
    /// compared by its bytes before anything counts it as changed or asks about backing it up. The digests found are
    /// recorded in the local hashes database (so the next tree aligns through a relation and nothing is read again),
    /// each part that names the bytes read is rewritten to their xxHash3 in memory, and the node's actions are worked
    /// out again. Writes no <see cref="DiskStateEntry"/>: that row belongs to the apply's transaction.
    /// </summary>
    /// <remarks>
    /// Runs without <see cref="_lock"/>, from <see cref="ResolveBackupOverflow"/>, the same class of work as the
    /// index. A part named by a SHA-1 that is not the bytes read keeps it: it is a different file, and the node's
    /// signature treats that like any other change.
    /// </remarks>
    internal async Task ResolveUnresolvedIdentities(
        Dictionary<GamePath, SyncNode> tree,
        GameInstallation installation,
        CancellationToken cancellationToken,
        SynchronizeLoadoutJob? job = null)
    {
        var wanted = tree.Where(kv => kv.Value.IdentityUnresolved && kv.Value.HaveDisk).Select(kv => kv.Key).ToList();
        if (wanted.Count == 0) return;

        var reads = await ReadAndSettle(wanted, installation, cancellationToken, ProgressOfReading(tree, wanted, job));
        var resolved = new Dictionary<GamePath, SyncNode>();
        foreach (var read in reads)
        {
            var node = tree[read.Path];
            if (NameByTheBytesRead(ref node, read)) node.DiskNamedByRead = true;
            resolved[read.Path] = node;
        }

        var resolver = _identityResolver.Scoped();
        foreach (var path in resolved.Keys.ToArray())
        {
            var node = resolved[path];
            MapActions(path, ref node, resolver);
            resolved[path] = node;
        }

        AdjustActions(resolved);
        foreach (var (path, read) in resolved)
        {
            // The file was read, so whether it differs is known; a part left as a SHA-1 is a different file.
            var node = read;
            node.IdentityUnresolved = false;
            tree[path] = node;
        }
    }

    /// <summary>
    /// Names a node's disk part by the xxHash3 of the bytes just read, and any previous or loadout part that is a
    /// SHA-1 of those same bytes. A part naming other bytes keeps its SHA-1: it is a different file. Both reads of a
    /// node, the early one and the one before writes, go through here so they cannot disagree about which parts
    /// follow. Returns whether the disk part's name changed.
    /// </summary>
    private static bool NameByTheBytesRead(ref SyncNode node, WholeRead read)
    {
        var xxHash3 = FileIdentity.XxHash3(read.Hashes.XxHash3);
        var renamed = node.Disk.Identity.MatchSameKind(xxHash3) != IdentityMatch.Same;
        node.Disk = node.Disk with { Identity = xxHash3 };
        if (node.HavePrevious && NamesTheBytesRead(node.Previous, read)) node.Previous = node.Previous with { Identity = xxHash3 };
        if (node.HaveLoadout && NamesTheBytesRead(node.Loadout, read)) node.Loadout = node.Loadout with { Identity = xxHash3 };
        return renamed;
    }

    private static bool NamesTheBytesRead(SyncNodePart part, WholeRead read)
        => part.Identity.Kind == HashKind.Sha1 && part.Size == read.Hashes.Size && read.Hashes.Sha1 == Sha1Value.From(part.Identity.Digest);

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
            if (await _identityResolver.StoreKeyAsync(_fileStore, node.Loadout.Identity, node.Loadout.Size) is not { } key) continue;

            var named = node with { Loadout = node.Loadout with { Identity = key } };
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

            if (node.Disk.Identity.Kind != HashKind.XxHash3) wanted.Add(path);
            else if (backsUp && path.LocationId == LocationId.Game && !_identityResolver.TryConvert(node.Disk.Identity, node.Disk.Size, HashKind.Sha1, out _)) wanted.Add(path);
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
