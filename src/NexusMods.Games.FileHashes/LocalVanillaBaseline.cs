using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Games.FileHashes;
using NexusMods.Abstractions.Games.FileHashes.Models;
using NexusMods.Abstractions.Steam.DTOs;
using NexusMods.Abstractions.Steam.Values;
using NexusMods.Hashing.xxHash3;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.MnemonicDB.Abstractions.TxFunctions;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Hashes;
using NexusMods.Sdk.NexusModsApi;
using OperatingSystem = NexusMods.Abstractions.Games.FileHashes.Values.OperatingSystem;

namespace NexusMods.Games.FileHashes;

/// <summary>
/// What <see cref="LocalVanillaBaseline"/> builds a baseline from.
/// </summary>
internal sealed record BaselineRequest
{
    /// <summary>The installation directory to classify.</summary>
    public required AbsolutePath GameRoot { get; init; }

    public required GameStore Store { get; init; }

    /// <summary>The app the manifests belong to; a manifest file does not carry it.</summary>
    public required AppId AppId { get; init; }

    /// <summary>
    /// The installation path exactly as <see cref="GameInstallMetadata.Path"/> holds it. The query joins on
    /// the string, and a path re-stringified from <see cref="GameRoot"/> can differ in the /home and
    /// /var/home spelling.
    /// </summary>
    public required string GamePath { get; init; }

    public required IReadOnlyList<string> LocatorIds { get; init; }

    /// <summary>The store's build id, when it has one.</summary>
    public string? VersionName { get; init; }

    /// <summary>The manifests of the installed depots that could be read. Empty produces a snapshot.</summary>
    public required IReadOnlyList<Manifest> Manifests { get; init; }

    /// <summary>False when an installed depot had no readable manifest, so its files cannot be told from mods.</summary>
    public required bool AllInstalledDepotsCovered { get; init; }

    /// <summary>Paths the synchronizer leaves alone (runtime output, backups): never hashed, never reported.</summary>
    public required Func<GamePath, bool> IsIgnored { get; init; }

    public required NexusModsGameId GameId { get; init; }

    public required OperatingSystem OperatingSystem { get; init; }
}

internal readonly record struct NonVanillaEntry(RelativePath Path, NonVanillaReason Reason, Size Size);

internal sealed record BaselineBuild(EntityId BaselineId, IReadOnlyList<NonVanillaEntry> NonVanilla);

/// <summary>
/// Classifies every file under a game directory against the depot manifests of the installed build and
/// writes the result to the local store: the files that match their manifest entry become the
/// vanilla file list, the files that don't are recorded as non-vanilla with the reason.
/// <para>
/// The size is compared before anything is read, because a size that differs from the manifest already
/// settles the file and game directories hold gigabytes; only files whose size matches are hashed, in
/// one pass that yields every hash a <see cref="HashRelation"/> needs. With no manifests at all the
/// directory is taken as it stands (a snapshot), and with only some depots covered, files no manifest
/// lists are taken as vanilla without a report, since they may belong to the uncovered depots.
/// </para>
/// </summary>
internal sealed class LocalVanillaBaseline
{
    private const int HashParallelism = 4;

    /// <summary>
    /// A baseline must classify at least one file in this many of the folder's files. Ignore rules cover
    /// runtime output and backups, which are a sliver of a game's files; this only trips when they swallow
    /// nearly everything.
    /// </summary>
    private const int MinClassifiedShare = 10;

    private readonly IFileSystem _fileSystem;
    private readonly ILogger<LocalVanillaBaseline> _logger;
    private readonly Func<AbsolutePath, Stream> _openRead;

    public LocalVanillaBaseline(IFileSystem fileSystem, ILogger<LocalVanillaBaseline> logger, Func<AbsolutePath, Stream>? openRead = null)
    {
        _fileSystem = fileSystem;
        _logger = logger;
        _openRead = openRead ?? (path => path.Read());
    }

    private readonly record struct IndexEntry(Size Size, Sha1Value Sha1, RelativePath Spelling, int ManifestIndex);

    private sealed record Candidate(string DiskPath, RelativePath Path, IndexEntry? Entry, Size Size, DateTime ModifiedUtc);

    private sealed record Hashed(Candidate Candidate, MultiHash Hash);

    public async Task<BaselineBuild> Build(BaselineRequest request, IConnection localStore, CancellationToken cancellationToken = default)
    {
        var kind = request.Manifests.Count == 0
            ? LocalBaselineKind.Snapshot
            : request.AllInstalledDepotsCovered ? LocalBaselineKind.DepotManifests : LocalBaselineKind.PartialDepotManifests;

        var index = BuildIndex(request.Manifests);
        var nonVanilla = new List<NonVanillaEntry>();
        var (candidates, walk) = Classify(request, kind, index, nonVanilla);
        EnsurePlausible(request, walk, candidates.Count + nonVanilla.Count);

        var stopwatch = Stopwatch.StartNew();
        var hashed = await HashCandidates(candidates, cancellationToken);
        _logger.LogInformation("Hashed {Count} of the files under {Root} in {Elapsed}", hashed.Count, request.GameRoot, stopwatch.Elapsed);

        using var tx = localStore.BeginTransaction();
        RetractPrevious(tx, localStore.Db, request);
        var baseline = new LocalBaseline.New(tx)
        {
            Store = request.Store,
            GamePath = request.GamePath,
            LocatorIds = request.LocatorIds.ToArray(),
            Kind = kind,
            VersionName = kind == LocalBaselineKind.Snapshot ? "" : request.VersionName ?? request.Manifests[0].ManifestId.Value.ToString(),
            BuiltAt = DateTimeOffset.UtcNow,
            FilesIds = [],
        };

        var hashRelations = new Dictionary<Hash, HashRelationId>();
        var pathIds = new List<EntityId>();
        var pathIdsByManifest = request.Manifests.Select(_ => new List<EntityId>()).ToArray();
        foreach (var (candidate, hash) in hashed.OrderBy(h => h.Candidate.DiskPath, StringComparer.Ordinal))
        {
            if (!StillUnchanged(candidate))
            {
                _logger.LogWarning("{Path} changed while it was being hashed, leaving it out of the baseline", candidate.DiskPath);
                continue;
            }

            if (candidate.Entry is { } entry && entry.Sha1 != hash.Sha1)
            {
                nonVanilla.Add(new NonVanillaEntry(entry.Spelling, NonVanillaReason.HashDiffers, hash.Size));
                continue;
            }

            var path = candidate.Entry?.Spelling ?? candidate.Path;
            var pathRelation = new PathHashRelation.New(tx)
            {
                Path = path,
                HashId = EnsureHashRelation(tx, localStore.Db, hashRelations, hash),
            };
            pathIds.Add(pathRelation.Id);
            if (candidate.Entry is { } matched) pathIdsByManifest[matched.ManifestIndex].Add(pathRelation.Id);
        }

        // A partial baseline writes them too: the version is the install's build id whatever the coverage,
        // and the readable manifests describe their depots exactly.
        if (kind != LocalBaselineKind.Snapshot)
            baseline.ManifestsIds = AddManifestRows(tx, request, pathIdsByManifest);

        baseline.FilesIds = pathIds;
        foreach (var entry in nonVanilla)
        {
            _ = new NonVanillaFile.New(tx)
            {
                BaselineId = baseline.Id,
                Path = entry.Path,
                Reason = entry.Reason,
                Size = entry.Size,
            };
        }

        var result = await tx.Commit();
        return new BaselineBuild(result[baseline.Id], nonVanilla);
    }

    private static Dictionary<RelativePath, IndexEntry> BuildIndex(IReadOnlyList<Manifest> manifests)
    {
        // Indexed by RelativePath, whose equality ignores the separator the depotcache spells paths with.
        var index = new Dictionary<RelativePath, IndexEntry>();
        for (var i = 0; i < manifests.Count; i++)
        {
            foreach (var file in manifests[i].Files)
                index.TryAdd(file.Path, new IndexEntry(file.Size, file.Hash, file.Path, i));
        }
        return index;
    }

    /// <param name="Entries">Everything the walk met under the root, symlinks included.</param>
    /// <param name="Files">The regular files among them, before any path was ignored.</param>
    private readonly record struct WalkCounts(int Entries, int Files);

    /// <summary>
    /// A classification that kept next to nothing of a folder that holds files means the walk or the ignore
    /// rule went wrong, and recording it would pass for a complete baseline that calls every file a mod
    /// (or, with no manifests, a game that ships nothing). It throws instead and records nothing, so the
    /// previous baseline stays and the next start tries again.
    /// </summary>
    private void EnsurePlausible(BaselineRequest request, WalkCounts walk, int classified)
    {
        // An empty folder is a game that is not installed; there is nothing to mislead anyone with.
        if (walk.Entries == 0) return;
        if (walk.Files > 0 && classified * MinClassifiedShare >= walk.Files) return;

        _logger.LogWarning("Classified {Classified} of the {Files} files ({Entries} entries) under {Root}, not recording a baseline from it",
            classified, walk.Files, walk.Entries, request.GameRoot);
        throw new InvalidOperationException(
            $"Only {classified} of the {walk.Files} files under {request.GameRoot} were classified, which cannot be a complete picture of the installation");
    }

    private (List<Candidate> Candidates, WalkCounts Walk) Classify(BaselineRequest request, LocalBaselineKind kind, Dictionary<RelativePath, IndexEntry> index, List<NonVanillaEntry> nonVanilla)
    {
        var candidates = new List<Candidate>();
        var entries = 0;
        var files = 0;
        var root = request.GameRoot.ToString();
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));

        while (pending.TryPop(out var directory))
        {
            foreach (var info in directory.EnumerateFileSystemInfos())
            {
                entries++;
                // A symlink is not a file the game shipped, and following one could leave the game directory.
                if (info.LinkTarget is not null) continue;
                if (info is DirectoryInfo subdirectory)
                {
                    pending.Push(subdirectory);
                    continue;
                }

                var file = (FileInfo)info;
                files++;
                var path = RelativePath.FromUnsanitizedInput(Path.GetRelativePath(root, file.FullName));
                if (request.IsIgnored(new GamePath(LocationId.Game, path))) continue;

                var size = Size.FromLong(file.Length);
                if (!index.TryGetValue(path, out var entry))
                {
                    // Without full coverage a file no manifest lists may belong to an uncovered depot.
                    if (kind == LocalBaselineKind.DepotManifests) nonVanilla.Add(new NonVanillaEntry(path, NonVanillaReason.NotInManifest, size));
                    else candidates.Add(new Candidate(file.FullName, path, null, size, file.LastWriteTimeUtc));
                    continue;
                }

                if (entry.Size != size)
                {
                    nonVanilla.Add(new NonVanillaEntry(entry.Spelling, NonVanillaReason.SizeDiffers, size));
                    continue;
                }

                candidates.Add(new Candidate(file.FullName, path, entry, size, file.LastWriteTimeUtc));
            }
        }

        return (candidates, new WalkCounts(entries, files));
    }

    private async Task<List<Hashed>> HashCandidates(List<Candidate> candidates, CancellationToken cancellationToken)
    {
        var results = new ConcurrentBag<Hashed>();
        var options = new ParallelOptions { MaxDegreeOfParallelism = HashParallelism, CancellationToken = cancellationToken };
        await Parallel.ForEachAsync(candidates, options, async (candidate, token) =>
        {
            try
            {
                await using var stream = _openRead(_fileSystem.FromUnsanitizedFullPath(candidate.DiskPath));
                results.Add(new Hashed(candidate, await MultiHasher.HashStream(stream, token)));
            }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
            {
                // Deleted after the directory walk, which is a rewrite like any other; other IO errors
                // still fail the build, because leaving those files out would shrink the baseline unseen.
                _logger.LogWarning("{Path} was removed before it was hashed, leaving it out of the baseline", candidate.DiskPath);
            }
        });
        return results.ToList();
    }

    /// <summary>
    /// A file that was rewritten between the stat before the read and the one after has a hash that
    /// describes neither version.
    /// </summary>
    private static bool StillUnchanged(Candidate candidate)
    {
        var after = new FileInfo(candidate.DiskPath);
        return after.Exists && Size.FromLong(after.Length) == candidate.Size && after.LastWriteTimeUtc == candidate.ModifiedUtc;
    }

    private static HashRelationId EnsureHashRelation(ITransaction tx, IDb db, Dictionary<Hash, HashRelationId> created, MultiHash hash)
    {
        if (created.TryGetValue(hash.XxHash3, out var known)) return known;

        var existing = HashRelation.FindByXxHash3(db, hash.XxHash3).FirstOrDefault();
        if (existing.IsValid()) return created[hash.XxHash3] = existing.HashRelationId;

        var relation = new HashRelation.New(tx)
        {
            MinimalHash = hash.MinimalHash,
            XxHash3 = hash.XxHash3,
            Sha1 = hash.Sha1,
            XxHash64 = hash.XxHash64,
            Md5 = hash.Md5,
            Size = hash.Size,
            Crc32 = hash.Crc32,
        };
        return created[hash.XxHash3] = relation.Id;
    }

    private static EntityId[] AddManifestRows(ITransaction tx, BaselineRequest request, List<EntityId>[] pathIdsByManifest)
    {
        var manifestIds = new List<EntityId>();
        for (var i = 0; i < request.Manifests.Count; i++)
        {
            var manifest = request.Manifests[i];
            // The branch is not recorded in a manifest, and lookups rank by ids rather than by name,
            // so an install on a beta branch is labelled "public" as well.
            var row = new SteamManifest.New(tx)
            {
                AppId = request.AppId,
                DepotId = manifest.DepotId,
                ManifestId = manifest.ManifestId,
                Name = "public",
                FilesIds = pathIdsByManifest[i],
            };
            manifestIds.Add(row.Id);
        }

        _ = new VersionDefinition.New(tx)
        {
            Name = request.VersionName ?? request.Manifests[0].ManifestId.Value.ToString(),
            OperatingSystem = request.OperatingSystem,
            GameId = request.GameId,
            GOG = [],
            Steam = request.Manifests.Select(m => m.ManifestId.Value.ToString()).ToArray(),
            EpicBuildIds = [],
            SteamManifestsIds = manifestIds,
        };
        return manifestIds.ToArray();
    }

    /// <summary>
    /// Deletes the rows of earlier builds for the same installation. The path rows are never shared
    /// between builds, so they go with it; hash rows are content-addressed and stay.
    /// </summary>
    private static void RetractPrevious(ITransaction tx, IDb db, BaselineRequest request)
    {
        foreach (var old in LocalBaseline.FindByGamePath(db, request.GamePath).Where(b => b.Store == request.Store))
        {
            foreach (var file in NonVanillaFile.FindByBaseline(db, old.Id)) tx.Delete(file.Id, recursive: false);
            foreach (var pathId in old.FilesIds) tx.Delete(pathId, recursive: false);

            var manifestIds = old.ManifestsIds.ToHashSet();
            foreach (var version in VersionDefinition.All(db).Where(v => v.SteamManifestsIds.Any(manifestIds.Contains)))
                tx.Delete(version.Id, recursive: false);
            // The manifests' path rows are the baseline's own, deleted above.
            foreach (var manifestId in manifestIds) tx.Delete(manifestId, recursive: false);

            tx.Delete(old.Id, recursive: false);
        }
    }
}
