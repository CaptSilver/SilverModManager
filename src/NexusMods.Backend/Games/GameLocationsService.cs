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

    public GameLocationsService(IServiceProvider serviceProvider)
    {
        _logger = serviceProvider.GetRequiredService<ILogger<GameLocationsService>>();
        _fileHashesService = serviceProvider.GetRequiredService<IFileHashesService>();
    }

    public async Task<IndexGameResult> IndexGame(
        GameInstallation installation,
        FrozenDictionary<GamePath, DiskStateEntry.ReadOnly> previousDiskState,
        IGamePathFilter filter,
        CancellationToken outerToken = default)
    {
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
                        var indexFileResult = await IndexFile(file, gamePath, token);
                        modifiedFiles[gamePath] = indexFileResult;
                    }
                }
                else
                {
                    var indexFileResult = await IndexFile(file, gamePath, token);
                    newFiles[gamePath] = indexFileResult;
                }
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Exception indexing file `{Path}`", file);
            }
        });

        var removedFiles = previousDiskState.Keys.Except(seenPaths.Keys).ToFrozenSet();
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
        CancellationToken cancellationToken)
    {
        var fingerprint = FileFingerprint.Of(file);

        var newHash = await HashFile(file, gamePath, cancellationToken);
        return new IndexFileResult(gamePath, newHash, fingerprint.Size, fingerprint.LastModified);
    }

    internal async ValueTask<Hash> HashFile(AbsolutePath file, GamePath gamePath, CancellationToken cancellationToken)
    {
        var existingHash = await GetExistingHash(_fileHashesService.Databases, file, gamePath, cancellationToken);
        if (existingHash.HasValue) return existingHash.Value;

        _logger.LogDebug("Didn't find matching hash data for file `{Path}`, falling back to a full hash", file);
        var hash = await file.XxHash3Async(token: cancellationToken);
        return hash;
    }

    /// <summary>
    /// Looks the file up in every hashes store, not only <see cref="IFileHashesService.Current"/>: the rows for the installed build
    /// may live in the store the app writes itself, and skipping it would mean fully hashing files the app has
    /// already hashed. Entity ids differ between stores, so each store is matched on its own and only the
    /// resulting hashes are compared. Stores that disagree on the hash give no answer, same as rows that
    /// disagree within one store.
    /// </summary>
    private static async ValueTask<Optional<Hash>> GetExistingHash(IReadOnlyList<IDb> hashesDbs, AbsolutePath file, GamePath gamePath, CancellationToken cancellationToken)
    {
        var candidates = new List<PathHashRelation.ReadOnly>();
        foreach (var hashesDb in hashesDbs)
        {
            candidates.AddRange(PathHashRelation.FindByPath(hashesDb, gamePath.Path));
        }

        if (candidates.Count == 0) return Optional<Hash>.None;

        var size = FileFingerprint.Of(file).Size;
        var minimalHash = await GetMinimalHash(file, cancellationToken);

        var foundHash = Optional<Hash>.None;
        foreach (var pathHashRelation in candidates)
        {
            var existingHashes = pathHashRelation.Hash;
            if (existingHashes.Size != size) continue;
            if (existingHashes.MinimalHash != minimalHash) continue;

            if (!foundHash.HasValue)
            {
                foundHash = existingHashes.XxHash3;
                continue;
            }

            if (existingHashes.XxHash3 != foundHash) return Optional<Hash>.None;
        }

        return foundHash;
    }

    private static async ValueTask<Hash> GetMinimalHash(AbsolutePath file, CancellationToken cancellationToken)
    {
        await using var fileStream = file.Read();
        return await MinimalHash.HashAsync<Hash, XxHash3, Xx3Hasher>(fileStream, cancellationToken: cancellationToken);
    }
}
