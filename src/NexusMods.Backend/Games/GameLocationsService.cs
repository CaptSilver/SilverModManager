using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.IO.Hashing;
using DynamicData.Kernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Games.FileHashes;
using NexusMods.Abstractions.Games.FileHashes.Models;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Hashing.xxHash3;
using NexusMods.Hashing.xxHash3.Paths;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Hashes;
using NexusMods.Sdk.IO;

namespace NexusMods.Backend.Games;

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
        CancellationToken outerToken = default)
    {
        // A baseline build commits all its rows at the end; indexing meanwhile would find none and read the whole folder.
        await _fileHashesService.WaitForLocalBaselineBuild(installation, outerToken);

        var tally = new IndexTally();
        var topLevelLocations = installation.Locations.GetTopLevelLocations();
        var enumerable = topLevelLocations.Where(kv => kv.Value.DirectoryExists()).SelectMany(kv => kv.Value.EnumerateFiles()).ToAsyncEnumerable();

        var seenPaths = new ConcurrentDictionary<GamePath, bool>();
        var newFiles = new ConcurrentDictionary<GamePath, IndexFileResult>();
        var modifiedFiles = new ConcurrentDictionary<GamePath, IndexFileResult>();

        await Parallel.ForEachAsync(enumerable, cancellationToken: outerToken, async (file, token) =>
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
                        var indexFileResult = await IndexFile(file, gamePath, tally, token);
                        modifiedFiles[gamePath] = indexFileResult;
                    }
                    else
                    {
                        tally.Unchanged();
                    }
                }
                else
                {
                    var indexFileResult = await IndexFile(file, gamePath, tally, token);
                    newFiles[gamePath] = indexFileResult;
                }
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Exception indexing file `{Path}`", file);
            }
        });

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
        AbsolutePath file,
        GamePath gamePath,
        IndexTally tally,
        CancellationToken cancellationToken)
    {
        var fingerprint = FileFingerprint.Of(file);

        var newHash = await HashFile(file, gamePath, tally, cancellationToken);
        return new IndexFileResult(gamePath, newHash, fingerprint.Size, fingerprint.LastModified);
    }

    internal ValueTask<Hash> HashFile(AbsolutePath file, GamePath gamePath, CancellationToken cancellationToken)
        => HashFile(file, gamePath, new IndexTally(), cancellationToken);

    private async ValueTask<Hash> HashFile(AbsolutePath file, GamePath gamePath, IndexTally tally, CancellationToken cancellationToken)
    {
        var lookup = await GetExistingHash(_fileHashesService.Databases, file, gamePath, cancellationToken);
        if (lookup.Hash.HasValue)
        {
            tally.FromStoredHash();
            return lookup.Hash.Value;
        }

        _logger.LogDebug("Didn't find matching hash data for file `{Path}` ({Reason}), falling back to a full hash", file, lookup.Miss);
        tally.FullyHashed(lookup.Miss, FileFingerprint.Of(file).Size);
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
