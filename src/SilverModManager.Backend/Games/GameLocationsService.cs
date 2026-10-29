using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.IO.Hashing;
using DynamicData.Kernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Games.FileHashes;
using SilverModManager.Abstractions.Games.FileHashes.Models;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using NexusMods.Hashing.xxHash3;
using NexusMods.Hashing.xxHash3.Paths;
using SilverModManager.MnemonicDB.Abstractions;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.IO;

namespace SilverModManager.Backend.Games;

internal class GameLocationsService : IGameLocationsService
{
    private readonly ILogger _logger;
    private readonly IFileHashesService _fileHashesService;
    private readonly IFileIdentityResolver _identityResolver;
    private readonly IFullFileHasher _fullHasher;

    public GameLocationsService(IServiceProvider serviceProvider)
    {
        _fullHasher = serviceProvider.GetService<IFullFileHasher>() ?? XxHash3FullFileHasher.Instance;
        _logger = serviceProvider.GetRequiredService<ILogger<GameLocationsService>>();
        _fileHashesService = serviceProvider.GetRequiredService<IFileHashesService>();
        _identityResolver = serviceProvider.GetRequiredService<IFileIdentityResolver>();
    }

    public async Task<IndexGameResult> IndexGame(
        GameInstallation installation,
        FrozenDictionary<GamePath, DiskStateEntry.ReadOnly> previousDiskState,
        IGamePathFilter filter,
        CancellationToken outerToken = default,
        Action<long, long>? progress = null,
        Func<IReadOnlyList<IndexFileResult>, ValueTask>? onBatch = null)
    {
        // A baseline build commits all its rows at the end; indexing meanwhile would find none and read the whole folder.
        await _fileHashesService.WaitForLocalBaselineBuild(installation, outerToken);

        var tally = new IndexTally();
        var topLevelLocations = installation.Locations.GetTopLevelLocations();
        // Listed up front so the total is known while the files are hashed.
        List<AbsolutePath> files;
        try
        {
            files = topLevelLocations.Where(kv => kv.Value.DirectoryExists()).SelectMany(kv => kv.Value.EnumerateFiles()).ToList();
        }
        catch (IOException e) when (e is not (FileNotFoundException or DirectoryNotFoundException))
        {
            throw new GameFolderUnreadableException(installation, $"Listing the files of {installation.Game.DisplayName}", e);
        }
        var enumerable = files.ToAsyncEnumerable();
        var totalFiles = files.Count;
        long filesDone = 0;
        progress?.Invoke(0, totalFiles);

        var seenPaths = new ConcurrentDictionary<GamePath, bool>();
        var newFiles = new ConcurrentDictionary<GamePath, IndexFileResult>();
        var modifiedFiles = new ConcurrentDictionary<GamePath, IndexFileResult>();
        var attestedFiles = _fileHashesService.AttestedFiles(installation);
        var batches = new BatchWriter(_fileHashesService, installation, onBatch, _logger);

        var options = new ParallelOptions { MaxDegreeOfParallelism = HashingLimits.FileReaders, CancellationToken = outerToken };
        await Parallel.ForEachAsync(enumerable, options, async (file, token) =>
        {
            var reads = new Reads();
            IndexFileResult? changed = null;
            try
            {
                var gamePath = installation.Locations.ToGamePath(file);
                if (filter.ShouldFilter(gamePath)) return;
                if (!seenPaths.TryAdd(gamePath, true)) return;

                var fingerprint = FileFingerprint.Of(file);
                var attested = attestedFiles.TryGetValue(gamePath, out var row) ? row : (AttestedLookup?)null;
                var attestedMatches = attested is { } a && a.Size == fingerprint.Size && a.ModifiedUtc.UtcTicks == fingerprint.LastModified.UtcTicks;

                if (previousDiskState.TryGetValue(gamePath, out var previousDiskStateEntry))
                {
                    var previous = previousDiskStateEntry.IdentityOf();
                    if (HasFileChanged(fingerprint, previousDiskStateEntry))
                    {
                        changed = modifiedFiles[gamePath] = await IndexChangedFile(installation, file, gamePath, fingerprint, attested, attestedMatches, tally, reads, token);
                    }
                    else if (previous.Kind == HashKind.XxHash3 && attestedMatches && !_identityResolver.TryConvert(previous, fingerprint.Size, HashKind.Sha1, out _))
                    {
                        // The index wrote this row before full reads recorded a relation, so nothing says whether the
                        // bytes are the store's or a mod's that the app deployed or adopted here. One read settles it.
                        var result = await IndexFile(installation, file, gamePath, tally, reads, attested, forceRead: true, token);
                        // Both are xxHash3 by construction: the branch is guarded by previous.Kind == XxHash3 and IndexFile always returns one.
                        if (result.Identity.Equals(previous)) tally.Unchanged();
                        else changed = modifiedFiles[gamePath] = result;
                    }
                    else if (previous.Kind != HashKind.XxHash3)
                    {
                        // A row that names the file by another kind is trusted only while the store's row still says the
                        // same; a rebuild that replaced it leaves the fingerprint rows to resolve the path.
                        // previous may be any non-xxHash3 kind; Equals is false across kinds, and a row of another kind than the store's SHA-1 is not trusted.
                        if (attestedMatches && attested is { } still && FileIdentity.Sha1(still.Sha1).Equals(previous))
                        {
                            tally.Unchanged();
                        }
                        else
                        {
                            changed = modifiedFiles[gamePath] = await IndexChangedFile(installation, file, gamePath, fingerprint, attested, attestedMatches, tally, reads, token);
                        }
                    }
                    else
                    {
                        // The app's own record of these bytes outranks a row the store wrote from a stat, so an
                        // unchanged xxHash3 row is never flipped to the store's SHA-1.
                        tally.Unchanged();
                    }
                }
                else
                {
                    changed = newFiles[gamePath] = await IndexChangedFile(installation, file, gamePath, fingerprint, attested, attestedMatches, tally, reads, token);
                }
            }
            catch (IOException e) when (e is not (FileNotFoundException or DirectoryNotFoundException) && GameFolderUnreadableException.IsDeviceFailure(e))
            {
                // A deleted or locked file, a socket or a symlink loop is one file, but a device that cannot
                // deliver bytes is the drive failing, and every file after it would fail the same way.
                throw new GameFolderUnreadableException(installation, file, e);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _logger.LogWarning(e, "Exception indexing file `{Path}`", file);
            }
            finally
            {
                progress?.Invoke(Interlocked.Increment(ref filesDone), totalFiles);
            }

            // Outside the catches above: a commit that fails ends the run instead of being logged as one bad file.
            await batches.Add(changed, reads);
        });

        await batches.Finish();

        var removedFiles = previousDiskState.Keys.Except(seenPaths.Keys).ToFrozenSet();
        tally.Log(_logger, installation.Game.DisplayName, newFiles.Count, modifiedFiles.Count, removedFiles.Count);
        var result = new IndexGameResult(
            NewFiles: newFiles.ToFrozenDictionary(),
            ModifiedFiles: modifiedFiles.ToFrozenDictionary(),
            RemovedFiles: removedFiles
        );

        return result;
    }

    private static bool HasFileChanged(FileFingerprint fingerprint, DiskStateEntry.ReadOnly previousState)
        => fingerprint.LastModified != previousState.LastModified || fingerprint.Size != previousState.Size;

    /// <summary>What reading one file left to remember, kept with that file so a batch records exactly its own.</summary>
    private sealed class Reads
    {
        public List<IndexedFileHash> Remembered { get; } = [];
        public List<(AttestedLookup Attested, MultiHash Hashes, FileFingerprint After)> Settled { get; } = [];
    }

    /// <summary>
    /// Hands settled files to the caller a batch at a time. The local-store rows of a batch's files are written
    /// before the caller commits the batch, so a crash between the two leaves rows the next run resolves from and
    /// never a disk state entry without the rows behind it.
    /// </summary>
    private sealed class BatchWriter(IFileHashesService fileHashes, GameInstallation installation, Func<IReadOnlyList<IndexFileResult>, ValueTask>? onBatch, ILogger logger)
    {
        private readonly object _gate = new();
        private readonly SemaphoreSlim _commit = new(1, 1);
        private List<IndexFileResult> _results = [];
        private List<Reads> _reads = [];

        public async ValueTask Add(IndexFileResult? result, Reads reads)
        {
            List<IndexFileResult>? full = null;
            List<Reads>? fullReads = null;
            lock (_gate)
            {
                _reads.Add(reads);
                if (result is { } r) _results.Add(r);
                if (_results.Count >= IGameLocationsService.IndexBatchSize) (full, fullReads) = Take();
            }

            if (full is not null && fullReads is not null) await Commit(full, fullReads);
        }

        /// <summary>The files that did not fill a batch, and the final call even when there are none.</summary>
        public async ValueTask Finish()
        {
            List<IndexFileResult> rest;
            List<Reads> restReads;
            lock (_gate) (rest, restReads) = Take();
            await Commit(rest, restReads);
        }

        private (List<IndexFileResult>, List<Reads>) Take()
        {
            var taken = (_results, _reads);
            (_results, _reads) = ([], []);
            return taken;
        }

        /// <summary>
        /// The rows only save a later index a read, so a store that cannot take them costs speed, not the index.
        /// </summary>
        private async ValueTask RecordFingerprints(IndexedFileHash[] remembered)
        {
            try
            {
                await fileHashes.RecordIndexedFiles(installation, remembered, CancellationToken.None);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning(e, "Could not save the fingerprint rows of {Count} indexed files; the next index reads them again", remembered.Length);
            }
        }

        private async ValueTask Commit(List<IndexFileResult> results, List<Reads> reads)
        {
            // A batch that has filled is written even if the run was cancelled meanwhile: the files in it were
            // already read, and the cancellation only has to stop the batches after it.
            await _commit.WaitAsync(CancellationToken.None);
            try
            {
                var remembered = reads.SelectMany(r => r.Remembered).ToArray();
                if (remembered.Length > 0) await RecordFingerprints(remembered);
                var settled = reads.SelectMany(r => r.Settled).ToArray();
                if (settled.Length > 0) await fileHashes.SettleAttested(installation, settled, CancellationToken.None);
                if (onBatch is not null) await onBatch(results);
            }
            catch (IOException e)
            {
                throw new GameFolderUnreadableException(installation, "Saving the indexed files", e);
            }
            finally
            {
                _commit.Release();
            }
        }
    }

    /// <summary>
    /// A file with no usable previous entry: given the store's SHA-1 when it matches the attested row for the path,
    /// else read by <see cref="IndexFile"/>. The row is the store's description of the path, confirmed by the size and
    /// modified time it was written with, so a file that still has both is not opened.
    /// </summary>
    private async ValueTask<IndexFileResult> IndexChangedFile(
        GameInstallation installation, AbsolutePath file, GamePath gamePath, FileFingerprint fingerprint,
        AttestedLookup? attested, bool attestedMatches, IndexTally tally, Reads reads, CancellationToken cancellationToken)
    {
        if (attestedMatches && attested is { } row)
        {
            tally.FromAttested();
            return new IndexFileResult(gamePath, FileIdentity.Sha1(row.Sha1), fingerprint.Size, fingerprint.LastModified);
        }

        return await IndexFile(installation, file, gamePath, tally, reads, attested, forceRead: false, cancellationToken);
    }

    private async ValueTask<IndexFileResult> IndexFile(
        GameInstallation installation,
        AbsolutePath file,
        GamePath gamePath,
        IndexTally tally,
        Reads reads,
        AttestedLookup? attested,
        bool forceRead,
        CancellationToken cancellationToken)
    {
        var fingerprint = FileFingerprint.Of(file);
        var allDigests = attested is not null || installation.LocatorResult.Store == GameStore.Steam;
        var newHash = await HashFile(installation, file, gamePath, fingerprint, tally, forceRead, allDigests, cancellationToken);

        // The rows outlive the disk state, so one is written only for bytes read under a fingerprint that held.
        // A file written during the read would otherwise keep a hash of mixed bytes under its old size and time.
        var after = FileFingerprint.Of(file);
        if (after != fingerprint)
        {
            fingerprint = after;
            newHash = await HashFile(installation, file, gamePath, fingerprint, tally, forceRead, allDigests, cancellationToken);
            after = FileFingerprint.Of(file);
            if (after != fingerprint)
            {
                _logger.LogDebug("File `{Path}` changed while it was read, not remembering its hash", file);
                return new IndexFileResult(gamePath, FileIdentity.XxHash3(newHash.XxHash3), fingerprint.Size, fingerprint.LastModified);
            }
        }

        // The bytes of an attested path settle the row, which also writes the fingerprint row and the relation.
        if (attested is { } row && newHash.Hashes is { } hashes) reads.Settled.Add((row, hashes, fingerprint));
        else if (!newHash.FromFingerprint) reads.Remembered.Add(new IndexedFileHash(gamePath, fingerprint.Size, fingerprint.LastModified, newHash.XxHash3, newHash.Hashes));
        return new IndexFileResult(gamePath, FileIdentity.XxHash3(newHash.XxHash3), fingerprint.Size, fingerprint.LastModified);
    }

    internal async ValueTask<Hash> HashFile(AbsolutePath file, GamePath gamePath, CancellationToken cancellationToken)
        => (await HashFile(null, file, gamePath, FileFingerprint.Of(file), new IndexTally(), forceRead: false, allDigests: false, cancellationToken)).XxHash3;

    /// <param name="XxHash3">The file's xxHash3.</param>
    /// <param name="Hashes">Every digest of the bytes, set when this call read the file in full to get them.</param>
    /// <param name="FromFingerprint">The hash came from the file's own fingerprint row, so recording it again would write what is already there.</param>
    private readonly record struct HashedFile(Hash XxHash3, MultiHash? Hashes = null, bool FromFingerprint = false);

    /// <param name="forceRead">Skip the remembered and stored hashes, because the caller needs every digest of the bytes.</param>
    /// <param name="allDigests">Take every digest in the same pass when a game-folder file is read in full.</param>
    private async ValueTask<HashedFile> HashFile(GameInstallation? installation, AbsolutePath file, GamePath gamePath, FileFingerprint fingerprint, IndexTally tally, bool forceRead, bool allDigests, CancellationToken cancellationToken)
    {
        var miss = StoredHashMiss.NoRowForPath;
        if (!forceRead)
        {
            if (installation is not null && _fileHashesService.TryGetFingerprintedHash(installation, gamePath, fingerprint.Size, fingerprint.LastModified, out var remembered))
            {
                tally.FromFingerprint();
                return new HashedFile(remembered, FromFingerprint: true);
            }

            var lookup = await GetExistingHash(_fileHashesService.Databases, file, gamePath, cancellationToken);
            if (lookup.Hash.HasValue)
            {
                tally.FromStoredHash();
                return new HashedFile(lookup.Hash.Value);
            }

            miss = lookup.Miss;
        }

        _logger.LogDebug("Didn't find matching hash data for file `{Path}` ({Reason}), falling back to a full hash", file, miss);
        tally.FullyHashed(miss, fingerprint.Size);

        // A full read of a game-folder file of a Steam install, or of a file the store vouches for, takes every digest
        // in the same pass. They are recorded as a relation, so the next baseline can resolve this file from its
        // hashes instead of reading it, and the manifest's SHA-1 can be joined to the xxHash3 the file is archived
        // under. Nothing reads a relation for the other stores, and MD5 and SHA-1 would only slow their first manage.
        if (gamePath.LocationId == LocationId.Game && allDigests)
        {
            var hashes = await _fullHasher.HashAll(file, cancellationToken);
            return new HashedFile(hashes.XxHash3, hashes);
        }

        return new HashedFile(await _fullHasher.Hash(file, cancellationToken));
    }

    /// <summary>
    /// Looks the file up in every hashes store, not only <see cref="IFileHashesService.Current"/>: the rows for the installed build
    /// may live in the store the app writes itself, and skipping it would mean fully hashing files the app has
    /// already hashed. Entity ids differ between stores, so each store is matched on its own and only the
    /// resulting hashes are compared. Stores that disagree on the hash give no answer, same as rows that
    /// disagree within one store.
    /// </summary>
    internal static async ValueTask<StoredHashLookup> GetExistingHash(IReadOnlyList<IDb> hashesDbs, AbsolutePath file, GamePath gamePath, CancellationToken cancellationToken)
    {
        var candidates = new List<PathHashRelation.ReadOnly>();
        foreach (var hashesDb in hashesDbs)
        {
            candidates.AddRange(PathHashRelation.FindByPath(hashesDb, gamePath.Path));
        }

        if (candidates.Count == 0) return StoredHashLookup.Missed(StoredHashMiss.NoRowForPath);

        var size = FileFingerprint.Of(file).Size;
        Optional<Hash> minimalHash = Optional<Hash>.None;

        var foundHash = Optional<Hash>.None;
        var sawSameSize = false;
        foreach (var pathHashRelation in candidates)
        {
            var existingHashes = pathHashRelation.Hash;
            if (existingHashes.Size != size) continue;
            sawSameSize = true;

            // The three blocks a minimal hash covers are the cheap proof that the stored row still describes this file.
            if (!minimalHash.HasValue) minimalHash = await GetMinimalHash(file, cancellationToken);
            if (existingHashes.MinimalHash != minimalHash.Value) continue;

            if (!foundHash.HasValue)
            {
                foundHash = existingHashes.XxHash3;
                continue;
            }

            if (existingHashes.XxHash3 != foundHash) return StoredHashLookup.Missed(StoredHashMiss.StoresDisagree);
        }

        if (foundHash.HasValue) return StoredHashLookup.Found(foundHash.Value);
        return StoredHashLookup.Missed(sawSameSize ? StoredHashMiss.ContentDiffers : StoredHashMiss.SizeDiffers);
    }

    private static async ValueTask<Hash> GetMinimalHash(AbsolutePath file, CancellationToken cancellationToken)
    {
        await using var fileStream = file.Read();
        return await MinimalHash.HashAsync<Hash, XxHash3, Xx3Hasher>(fileStream, cancellationToken: cancellationToken);
    }
}
