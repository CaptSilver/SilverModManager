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

        var options = new ParallelOptions { MaxDegreeOfParallelism = HashingLimits.FileReaders, CancellationToken = outerToken };
        await Parallel.ForEachAsync(enumerable, options, async (file, token) =>
        {
            try
            {
                var gamePath = installation.Locations.ToGamePath(file);
                if (filter.ShouldFilter(gamePath)) return;
                if (!seenPaths.TryAdd(gamePath, true)) return;

                if (previousDiskState.TryGetValue(gamePath, out var previousDiskStateEntry))
                {
                    if (HasFileChanged(file, previousDiskStateEntry))
                    {
                        var indexFileResult = await IndexFile(installation, file, gamePath, tally, readFiles, token);
                        modifiedFiles[gamePath] = indexFileResult;
                    }
                    else
                    {
                        tally.Unchanged();
                    }
                }
                else
                {
                    var indexFileResult = await IndexFile(installation, file, gamePath, tally, readFiles, token);
                    newFiles[gamePath] = indexFileResult;
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

        var removedFiles = previousDiskState.Keys.Except(seenPaths.Keys).ToFrozenSet();
        tally.Log(_logger, installation.Game.DisplayName, newFiles.Count, modifiedFiles.Count, removedFiles.Count);
        var result = new IndexGameResult(
            NewFiles: newFiles.ToFrozenDictionary(),
            ModifiedFiles: modifiedFiles.ToFrozenDictionary(),
            RemovedFiles: removedFiles
        );

        return result;
    }

    private static bool HasFileChanged(AbsolutePath file, DiskStateEntry.ReadOnly previousState)
    {
        var fingerprint = FileFingerprint.Of(file);
        return fingerprint.LastModified != previousState.LastModified || fingerprint.Size != previousState.Size;
    }

    private async ValueTask<IndexFileResult> IndexFile(
        GameInstallation installation,
        AbsolutePath file,
        GamePath gamePath,
        IndexTally tally,
        ConcurrentBag<IndexedFileHash> readFiles,
        CancellationToken cancellationToken)
    {
        var fingerprint = FileFingerprint.Of(file);
        var newHash = await HashFile(installation, file, gamePath, fingerprint, tally, cancellationToken);

        // The rows outlive the disk state, so one is written only for bytes read under a fingerprint that held.
        // A file written during the read would otherwise keep a hash of mixed bytes under its old size and time.
        var after = FileFingerprint.Of(file);
        if (after != fingerprint)
        {
            fingerprint = after;
            newHash = await HashFile(installation, file, gamePath, fingerprint, tally, cancellationToken);
            after = FileFingerprint.Of(file);
            if (after != fingerprint)
            {
                _logger.LogDebug("File `{Path}` changed while it was read, not remembering its hash", file);
                return new IndexFileResult(gamePath, newHash, fingerprint.Size, fingerprint.LastModified);
            }
        }

        readFiles.Add(new IndexedFileHash(gamePath, fingerprint.Size, fingerprint.LastModified, newHash));
        return new IndexFileResult(gamePath, newHash, fingerprint.Size, fingerprint.LastModified);
    }

    internal ValueTask<Hash> HashFile(AbsolutePath file, GamePath gamePath, CancellationToken cancellationToken)
        => HashFile(null, file, gamePath, FileFingerprint.Of(file), new IndexTally(), cancellationToken);

    private async ValueTask<Hash> HashFile(GameInstallation? installation, AbsolutePath file, GamePath gamePath, FileFingerprint fingerprint, IndexTally tally, CancellationToken cancellationToken)
    {
        if (installation is not null && _fileHashesService.TryGetFingerprintedHash(installation, gamePath, fingerprint.Size, fingerprint.LastModified, out var remembered))
        {
            tally.FromFingerprint();
            return remembered;
        }

        var lookup = await GetExistingHash(_fileHashesService.Databases, file, gamePath, cancellationToken);
        if (lookup.Hash.HasValue)
        {
            tally.FromStoredHash();
            return lookup.Hash.Value;
        }

        _logger.LogDebug("Didn't find matching hash data for file `{Path}` ({Reason}), falling back to a full hash", file, lookup.Miss);
        tally.FullyHashed(lookup.Miss, fingerprint.Size);
        return await _fullHasher.Hash(file, cancellationToken);
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
