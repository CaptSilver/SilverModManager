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
    private readonly IFullFileHasher _fullHasher;

    public GameLocationsService(IServiceProvider serviceProvider)
    {
        _fullHasher = serviceProvider.GetService<IFullFileHasher>() ?? XxHash3FullFileHasher.Instance;
        _logger = serviceProvider.GetRequiredService<ILogger<GameLocationsService>>();
        _fileHashesService = serviceProvider.GetRequiredService<IFileHashesService>();
    }

    public async Task<IndexGameResult> IndexGame(
        GameInstallation installation,
        FrozenDictionary<GamePath, DiskStateEntry.ReadOnly> previousDiskState,
        IGamePathFilter filter,
        CancellationToken outerToken = default,
        Action<long, long>? progress = null)
    {
        // A baseline build commits all its rows at the end; indexing meanwhile would find none and read the whole folder.
        await _fileHashesService.WaitForLocalBaselineBuild(installation, outerToken);

        var tally = new IndexTally();
        var topLevelLocations = installation.Locations.GetTopLevelLocations();
        // Listed up front so the total is known while the files are hashed.
        var files = topLevelLocations.Where(kv => kv.Value.DirectoryExists()).SelectMany(kv => kv.Value.EnumerateFiles()).ToList();
        var enumerable = files.ToAsyncEnumerable();
        var totalFiles = files.Count;
        long filesDone = 0;
        progress?.Invoke(0, totalFiles);

        var seenPaths = new ConcurrentDictionary<GamePath, bool>();
        var newFiles = new ConcurrentDictionary<GamePath, IndexFileResult>();
        var modifiedFiles = new ConcurrentDictionary<GamePath, IndexFileResult>();
        var readFiles = new ConcurrentBag<IndexedFileHash>();
        var settled = new ConcurrentBag<(AttestedLookup Attested, MultiHash Hashes, FileFingerprint After)>();
        var attestedFiles = _fileHashesService.AttestedFiles(installation);
        var reads = new Reads(readFiles, settled);

        var options = new ParallelOptions { MaxDegreeOfParallelism = HashingLimits.FileReaders, CancellationToken = outerToken };
        await Parallel.ForEachAsync(enumerable, options, async (file, token) =>
        {
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
                        modifiedFiles[gamePath] = await IndexChangedFile(installation, file, gamePath, fingerprint, attested, attestedMatches, tally, reads, token);
                    }
                    else if (previous.Kind == HashKind.XxHash3 && attestedMatches && !_fileHashesService.TryFindRelation(previous.XxHash3(), out _))
                    {
                        // The index wrote this row before full reads recorded a relation, so nothing says whether the
                        // bytes are the store's or a mod's that the app deployed or adopted here. One read settles it.
                        var result = await IndexFile(installation, file, gamePath, tally, reads, attested, forceRead: true, token);
                        if (result.Identity == previous) tally.Unchanged();
                        else modifiedFiles[gamePath] = result;
                    }
                    else if (previous.Kind != HashKind.XxHash3)
                    {
                        // A row that names the file by another kind is trusted only while the store's row still says the
                        // same; a rebuild that replaced it leaves the fingerprint rows to resolve the path.
                        if (attestedMatches && attested is { } still && FileIdentity.Sha1(still.Sha1) == previous)
                        {
                            tally.Unchanged();
                        }
                        else
                        {
                            modifiedFiles[gamePath] = await IndexChangedFile(installation, file, gamePath, fingerprint, attested, attestedMatches, tally, reads, token);
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
                    newFiles[gamePath] = await IndexChangedFile(installation, file, gamePath, fingerprint, attested, attestedMatches, tally, reads, token);
                }
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Exception indexing file `{Path}`", file);
            }
            finally
            {
                progress?.Invoke(Interlocked.Increment(ref filesDone), totalFiles);
            }
        });

        await _fileHashesService.RecordIndexedFiles(installation, readFiles.ToArray(), outerToken);
        await _fileHashesService.SettleAttested(installation, settled.ToArray(), outerToken);

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

    /// <summary>What the parallel loop collects for the two writes that follow it.</summary>
    private sealed record Reads(ConcurrentBag<IndexedFileHash> Remembered, ConcurrentBag<(AttestedLookup Attested, MultiHash Hashes, FileFingerprint After)> Settled);

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
        var newHash = await HashFile(installation, file, gamePath, fingerprint, tally, forceRead, cancellationToken);

        // The rows outlive the disk state, so one is written only for bytes read under a fingerprint that held.
        // A file written during the read would otherwise keep a hash of mixed bytes under its old size and time.
        var after = FileFingerprint.Of(file);
        if (after != fingerprint)
        {
            fingerprint = after;
            newHash = await HashFile(installation, file, gamePath, fingerprint, tally, forceRead, cancellationToken);
            after = FileFingerprint.Of(file);
            if (after != fingerprint)
            {
                _logger.LogDebug("File `{Path}` changed while it was read, not remembering its hash", file);
                return new IndexFileResult(gamePath, FileIdentity.XxHash3(newHash.XxHash3), fingerprint.Size, fingerprint.LastModified);
            }
        }

        // The bytes of an attested path settle the row, which also writes the fingerprint row and the relation.
        if (attested is { } row && newHash.Hashes is { } hashes) reads.Settled.Add((row, hashes, fingerprint));
        else reads.Remembered.Add(new IndexedFileHash(gamePath, fingerprint.Size, fingerprint.LastModified, newHash.XxHash3, newHash.Hashes));
        return new IndexFileResult(gamePath, FileIdentity.XxHash3(newHash.XxHash3), fingerprint.Size, fingerprint.LastModified);
    }

    internal async ValueTask<Hash> HashFile(AbsolutePath file, GamePath gamePath, CancellationToken cancellationToken)
        => (await HashFile(null, file, gamePath, FileFingerprint.Of(file), new IndexTally(), forceRead: false, cancellationToken)).XxHash3;

    /// <param name="XxHash3">The file's xxHash3.</param>
    /// <param name="Hashes">Every digest of the bytes, set when this call read the file in full to get them.</param>
    private readonly record struct HashedFile(Hash XxHash3, MultiHash? Hashes = null);

    /// <param name="forceRead">Skip the remembered and stored hashes, because the caller needs every digest of the bytes.</param>
    private async ValueTask<HashedFile> HashFile(GameInstallation? installation, AbsolutePath file, GamePath gamePath, FileFingerprint fingerprint, IndexTally tally, bool forceRead, CancellationToken cancellationToken)
    {
        var miss = StoredHashMiss.NoRowForPath;
        if (!forceRead)
        {
            if (installation is not null && _fileHashesService.TryGetFingerprintedHash(installation, gamePath, fingerprint.Size, fingerprint.LastModified, out var remembered))
            {
                tally.FromFingerprint();
                return new HashedFile(remembered);
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

        // A full read of a game-folder file takes every digest in the same pass. They are recorded as a relation, so
        // the next baseline can resolve this file from its hashes instead of reading it, and the store's SHA-1 can
        // be joined to the xxHash3 the file is archived under. Other locations only need the xxHash3.
        if (gamePath.LocationId == LocationId.Game)
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
