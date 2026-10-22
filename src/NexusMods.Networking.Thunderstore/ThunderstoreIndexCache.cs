using System.Text.Json;
using JetBrains.Annotations;
using NexusMods.Networking.Thunderstore.DTOs;
using NexusMods.Paths;
using NexusMods.Sdk.Hashes;
using NexusMods.Sdk.Settings;

namespace NexusMods.Networking.Thunderstore;

/// <summary>
/// Keeps each community's package-listing index on disk: <c>&lt;CacheRoot&gt;/&lt;community&gt;/index.json</c> and the
/// chunk bodies, as received, under <c>chunks/&lt;sha256&gt;.json.gz</c>. Chunks are content-addressed, so a refresh
/// only downloads the ones it does not already have. Each community's catalog is built once per index and shared.
/// </summary>
[PublicAPI]
public sealed class ThunderstoreIndexCache
{
    private const string IndexFileName = "index.json";
    private const string ChunksFolderName = "chunks";
    private const string ChunkExtension = ".json.gz";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly IThunderstoreApi _api;
    private readonly IFileSystem _fileSystem;
    private readonly Func<ThunderstoreSettings> _settings;
    private readonly TimeProvider _time;
    // Load holds this too: a refresh rewrites index.json and deletes orphaned chunks, which a concurrent read would trip over.
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<string, (string IndexSha256, ThunderstoreCatalog Catalog)> _catalogs = new(StringComparer.Ordinal);

    public ThunderstoreIndexCache(IThunderstoreApi api, IFileSystem fileSystem, ISettingsManager settings, TimeProvider time)
        : this(api, fileSystem, () => settings.Get<ThunderstoreSettings>(), time) { }

    internal ThunderstoreIndexCache(IThunderstoreApi api, IFileSystem fileSystem, Func<ThunderstoreSettings> settings, TimeProvider time)
    {
        _api = api;
        _fileSystem = fileSystem;
        _settings = settings;
        _time = time;
    }

    /// <summary>
    /// True when the community has no cached index or it is older than <see cref="ThunderstoreSettings.RefreshAge"/>.
    /// </summary>
    public bool IsStale(string community)
    {
        var stored = ReadStoredIndex(community);
        return stored is null || _time.GetUtcNow() - stored.FetchedAt > _settings().RefreshAge;
    }

    /// <summary>
    /// Brings the cached index up to date. Nothing on disk changes unless every new chunk downloaded and verified,
    /// so a failed refresh leaves the previous index loadable.
    /// </summary>
    /// <exception cref="ThunderstoreBlobIntegrityException">A chunk's bytes do not match the digest in its URL.</exception>
    public async Task Refresh(string community, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var index = await _api.FetchListingIndex(community, cancellationToken);
            var stored = ReadStoredIndex(community);

            var chunkDigests = index.Chunks.Select(ChunkDigest).ToArray();
            var missing = new List<int>();
            for (var i = 0; i < chunkDigests.Length; i++)
            {
                if (!await IsChunkIntact(ChunkPath(community, chunkDigests[i]), chunkDigests[i], cancellationToken)) missing.Add(i);
            }

            if (stored is not null && stored.Sha256 == index.Index.Sha256.ToString() && missing.Count == 0)
            {
                WriteIndex(community, stored with { FetchedAt = _time.GetUtcNow() });
                return;
            }

            foreach (var i in missing)
            {
                var path = ChunkPath(community, chunkDigests[i]);
                var bytes = await _api.DownloadBlob(index.Chunks[i], cancellationToken);
                ChunkReader.Verify(bytes, index.Chunks[i]);

                path.Parent.CreateDirectory();
                await path.WriteAllBytesAsync(bytes);
            }

            WriteIndex(community, new StoredIndex
            {
                Sha256 = index.Index.Sha256.ToString(),
                Uri = index.Index.Uri,
                Chunks = index.Chunks.ToList(),
                FetchedAt = _time.GetUtcNow(),
            });

            var referenced = chunkDigests.Select(d => ChunkPath(community, d).FileName).ToHashSet(StringComparer.Ordinal);
            var chunksFolder = ChunksFolder(community);
            if (!chunksFolder.DirectoryExists()) return;
            foreach (var file in chunksFolder.EnumerateFiles().ToArray())
            {
                if (!referenced.Contains(file.FileName)) file.Delete();
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// The community's catalog, read from the cached chunks (each verified against its digest again) the first time and
    /// after every refresh that brought a new index; otherwise the instance already in memory.
    /// </summary>
    /// <exception cref="ThunderstoreIndexUnavailableException">The community has no cached index, or a cached chunk is
    /// missing or damaged. A refresh repairs either.</exception>
    public async Task<ThunderstoreCatalog> Load(string community, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var stored = ReadStoredIndex(community) ?? throw new ThunderstoreIndexUnavailableException(community);
            if (_catalogs.TryGetValue(community, out var held) && held.IndexSha256 == stored.Sha256) return held.Catalog;

            var catalog = new ThunderstoreCatalog(community, await ReadChunks(community, stored, cancellationToken));
            _catalogs[community] = (stored.Sha256, catalog);
            return catalog;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<List<PackageListing>> ReadChunks(string community, StoredIndex stored, CancellationToken cancellationToken)
    {
        var packages = new List<PackageListing>();
        foreach (var chunk in stored.Chunks)
        {
            var path = ChunkPath(community, ChunkDigest(chunk));
            if (!path.FileExists) throw new ThunderstoreIndexUnavailableException(community, new FileNotFoundException("Cached chunk is missing", path.GetFullPath()));

            try
            {
                await using var file = path.Read();
                await using var gunzipped = await ChunkReader.OpenVerified(file, chunk, cancellationToken);
                await foreach (var package in ChunkReader.ReadPackages(gunzipped, chunk, cancellationToken))
                    packages.Add(package);
            }
            catch (Exception e) when (e is ThunderstoreBlobIntegrityException or JsonException or InvalidDataException)
            {
                throw new ThunderstoreIndexUnavailableException(community, e);
            }
        }

        return packages;
    }

    // A file that is present is not enough: a write cut short leaves a truncated chunk that would otherwise be kept forever.
    private static async Task<bool> IsChunkIntact(AbsolutePath path, Sha256Value digest, CancellationToken cancellationToken)
    {
        if (!path.FileExists) return false;
        var bytes = await path.ReadAllBytesAsync(cancellationToken);
        return Sha256Hasher.Hash(bytes) == digest;
    }

    private static Sha256Value ChunkDigest(Uri chunk)
        => ChunkReader.DigestFromUri(chunk) ?? throw new JsonException($"Chunk URL {chunk} has no sha256 segment to name it by");

    private AbsolutePath CommunityFolder(string community)
        => _settings().CacheRoot.ToPath(_fileSystem) / community;

    private AbsolutePath ChunksFolder(string community) => CommunityFolder(community) / ChunksFolderName;

    private AbsolutePath ChunkPath(string community, Sha256Value digest)
        => ChunksFolder(community) / $"{digest}{ChunkExtension}";

    private StoredIndex? ReadStoredIndex(string community)
    {
        var path = CommunityFolder(community) / IndexFileName;
        if (!path.FileExists) return null;

        try
        {
            using var stream = path.Read();
            return JsonSerializer.Deserialize<StoredIndex>(stream, JsonOptions);
        }
        catch (JsonException)
        {
            // A half-written or hand-edited file is the same as no cache: the next refresh rewrites it.
            return null;
        }
    }

    private void WriteIndex(string community, StoredIndex index)
    {
        var path = CommunityFolder(community) / IndexFileName;
        path.Parent.CreateDirectory();
        using var stream = path.Create();
        JsonSerializer.Serialize(stream, index, JsonOptions);
    }

    private sealed record StoredIndex
    {
        public required string Sha256 { get; init; }
        public required Uri Uri { get; init; }
        public required List<Uri> Chunks { get; init; }
        public required DateTimeOffset FetchedAt { get; init; }
    }
}
