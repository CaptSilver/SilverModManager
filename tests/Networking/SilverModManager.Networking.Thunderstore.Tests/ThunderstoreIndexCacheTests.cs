using FluentAssertions;
using NexusMods.Networking.Thunderstore.Tests.Fixtures;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.Thunderstore;
using Xunit;

namespace NexusMods.Networking.Thunderstore.Tests;

public class ThunderstoreIndexCacheTests
{
    private const string IndexUrl = "https://thunderstore.io/c/repo/api/v1/package-listing-index/";

    private readonly InMemoryFileSystem _fileSystem = new();
    private readonly RoutingHandler _handler = new();
    private readonly TestClock _clock = new();
    private readonly ThunderstoreSettings _settings;
    private readonly ThunderstoreIndexCache _cache;

    public ThunderstoreIndexCacheTests()
    {
        _settings = new ThunderstoreSettings { CacheRoot = new ConfigurablePath(KnownPath.EntryDirectory, "thunderstore-cache") };
        _cache = new ThunderstoreIndexCache(new ThunderstoreApi(new HttpClient(_handler)), _fileSystem, () => _settings, _clock);
    }

    private AbsolutePath Root => _settings.CacheRoot.ToPath(_fileSystem) / "repo";

    /// <summary>
    /// Serves a chunk at a content-addressed URL and returns that URL.
    /// </summary>
    private string ServeChunk(string json, string? urlDigest = null)
    {
        var body = Gzip.Compress(json);
        var url = $"https://cdn.example/sha256/{urlDigest ?? Gzip.HexDigest(body)}/chunk";
        _handler.Add(url, body, "application/octet-stream");
        return url;
    }

    /// <summary>
    /// Serves an index naming the chunks and returns its digest.
    /// </summary>
    private string ServeIndex(params string[] chunkUrls)
    {
        var body = Gzip.Compress("[" + string.Join(",", chunkUrls.Select(u => $"\"{u}\"")) + "]");
        var hex = Gzip.HexDigest(body);
        _handler.Add(IndexUrl, body, "application/octet-stream", new Uri($"https://cdn.example/sha256/{hex}/index"));
        return hex;
    }

    private static string Digest(string chunkUrl) => new Uri(chunkUrl).Segments[2].TrimEnd('/');

    private AbsolutePath ChunkFile(string chunkUrl) => Root / "chunks" / $"{Digest(chunkUrl)}.json.gz";

    private string RepoLibChunk() => ServeChunk($"[{ThunderstoreJson.RepoLib}]");
    private string ConfigChunk() => ServeChunk($"[{ThunderstoreJson.RepoConfig}]");
    private string BepInExChunk() => ServeChunk($"[{ThunderstoreJson.BepInExPack}]");

    [Fact]
    public async Task Refresh_FirstTime_WritesIndexAndChunksNamedByDigest()
    {
        var a = RepoLibChunk();
        var b = ConfigChunk();
        ServeIndex(a, b);

        await _cache.Refresh("repo", CancellationToken.None);

        (Root / "index.json").FileExists.Should().BeTrue();
        ChunkFile(a).FileExists.Should().BeTrue();
        ChunkFile(b).FileExists.Should().BeTrue();
        (Root / "chunks").EnumerateFiles().Should().HaveCount(2);
    }

    [Fact]
    public async Task Refresh_SameIndexDigest_RequestsOnlyTheIndex()
    {
        ServeIndex(RepoLibChunk(), ConfigChunk());
        await _cache.Refresh("repo", CancellationToken.None);
        _handler.Requests.Clear();
        _clock.Now += TimeSpan.FromMinutes(5);

        await _cache.Refresh("repo", CancellationToken.None);

        _handler.Requests.Should().Equal(new Uri(IndexUrl));
    }

    [Fact]
    public async Task Refresh_SameIndexDigest_UpdatesFetchTime()
    {
        ServeIndex(RepoLibChunk());
        await _cache.Refresh("repo", CancellationToken.None);
        _clock.Now += _settings.RefreshAge + TimeSpan.FromMinutes(1);
        _cache.IsStale("repo").Should().BeTrue();

        await _cache.Refresh("repo", CancellationToken.None);

        _cache.IsStale("repo").Should().BeFalse();
    }

    [Fact]
    public async Task Refresh_NewIndexSharingAChunk_DownloadsOnlyTheNewChunkAndDeletesTheOrphan()
    {
        var kept = RepoLibChunk();
        var dropped = ConfigChunk();
        ServeIndex(kept, dropped);
        await _cache.Refresh("repo", CancellationToken.None);
        _handler.Requests.Clear();

        var added = BepInExChunk();
        ServeIndex(kept, added);
        await _cache.Refresh("repo", CancellationToken.None);

        _handler.Requests.Should().Equal(new Uri(IndexUrl), new Uri(added));
        ChunkFile(kept).FileExists.Should().BeTrue();
        ChunkFile(added).FileExists.Should().BeTrue();
        ChunkFile(dropped).FileExists.Should().BeFalse();
    }

    [Fact]
    public async Task Refresh_ChunkDigestMismatch_ThrowsWithoutWritingAndKeepsPreviousIndex()
    {
        var good = RepoLibChunk();
        ServeIndex(good);
        await _cache.Refresh("repo", CancellationToken.None);

        var corrupt = ServeChunk($"[{ThunderstoreJson.RepoConfig}]", urlDigest: new string('0', 64));
        ServeIndex(good, corrupt);

        var act = () => _cache.Refresh("repo", CancellationToken.None);

        await act.Should().ThrowAsync<ThunderstoreBlobIntegrityException>();
        ChunkFile(corrupt).FileExists.Should().BeFalse();
        var catalog = await _cache.Load("repo", CancellationToken.None);
        catalog.Count.Should().Be(1);
    }

    [Fact]
    public async Task Refresh_RequestFails_LeavesFilesAndLoadReturnsOldCatalog()
    {
        var chunk = RepoLibChunk();
        ServeIndex(chunk);
        await _cache.Refresh("repo", CancellationToken.None);

        // Dropping the route makes the handler answer 404, which the client reports as an HttpRequestException.
        var failing = new ThunderstoreIndexCache(new ThunderstoreApi(new HttpClient(new RoutingHandler())), _fileSystem, () => _settings, _clock);
        var act = () => failing.Refresh("repo", CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
        ChunkFile(chunk).FileExists.Should().BeTrue();
        var catalog = await failing.Load("repo", CancellationToken.None);
        catalog.Count.Should().Be(1);
    }

    [Fact]
    public async Task Load_ReadsAllChunksIntoOneCatalog()
    {
        ServeIndex(RepoLibChunk(), ConfigChunk());
        await _cache.Refresh("repo", CancellationToken.None);

        var catalog = await _cache.Load("repo", CancellationToken.None);

        catalog.Community.Should().Be("repo");
        catalog.Count.Should().Be(2);
    }

    [Fact]
    public async Task Load_NothingCached_ThrowsNamingTheCommunity()
    {
        var act = () => _cache.Load("peak", CancellationToken.None);

        (await act.Should().ThrowAsync<ThunderstoreIndexUnavailableException>()).WithMessage("*peak*");
    }

    [Fact]
    public async Task Refresh_CorruptChunkOnDisk_DownloadsItAgain()
    {
        var damaged = RepoLibChunk();
        var intact = ConfigChunk();
        ServeIndex(damaged, intact);
        await _cache.Refresh("repo", CancellationToken.None);
        await ChunkFile(damaged).WriteAllBytesAsync("truncated"u8.ToArray());
        _handler.Requests.Clear();

        await _cache.Refresh("repo", CancellationToken.None);

        _handler.Requests.Should().Equal(new Uri(IndexUrl), new Uri(damaged));
        (await _cache.Load("repo", CancellationToken.None)).Count.Should().Be(2);
    }

    [Fact]
    public async Task Load_CorruptChunkOnDisk_ThrowsUnavailableNamingTheCommunity()
    {
        var chunk = RepoLibChunk();
        ServeIndex(chunk);
        await _cache.Refresh("repo", CancellationToken.None);
        await ChunkFile(chunk).WriteAllBytesAsync("truncated"u8.ToArray());

        var act = () => _cache.Load("repo", CancellationToken.None);

        (await act.Should().ThrowAsync<ThunderstoreIndexUnavailableException>())
            .WithMessage("*repo*")
            .WithInnerException<ThunderstoreBlobIntegrityException>();
    }

    [Fact]
    public async Task Refresh_IndexWithNoChunks_WritesAnEmptyCatalog()
    {
        // The real file system, unlike the in-memory one, throws when listing a folder that was never created.
        var root = Path.Combine(Path.GetTempPath(), $"thunderstore-cache-{Guid.NewGuid():N}");
        try
        {
            var settings = new ThunderstoreSettings { CacheRoot = new ConfigurablePath(KnownPath.TempDirectory, Path.GetFileName(root)) };
            var cache = new ThunderstoreIndexCache(new ThunderstoreApi(new HttpClient(_handler)), FileSystem.Shared, () => settings, _clock);
            ServeIndex();

            await cache.Refresh("repo", CancellationToken.None);

            (await cache.Load("repo", CancellationToken.None)).Count.Should().Be(0);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Load_SameIndex_ReturnsTheSameCatalog()
    {
        ServeIndex(RepoLibChunk());
        await _cache.Refresh("repo", CancellationToken.None);
        var first = await _cache.Load("repo", CancellationToken.None);

        await _cache.Refresh("repo", CancellationToken.None);

        (await _cache.Load("repo", CancellationToken.None)).Should().BeSameAs(first);
    }

    [Fact]
    public async Task Load_AfterRefreshWithANewIndex_ReturnsANewCatalog()
    {
        var chunk = RepoLibChunk();
        ServeIndex(chunk);
        await _cache.Refresh("repo", CancellationToken.None);
        var first = await _cache.Load("repo", CancellationToken.None);

        ServeIndex(chunk, ConfigChunk());
        await _cache.Refresh("repo", CancellationToken.None);
        var second = await _cache.Load("repo", CancellationToken.None);

        second.Should().NotBeSameAs(first);
        second.Count.Should().Be(2);
    }

    [Fact]
    public async Task IsStale_FollowsTheConfiguredRefreshAge()
    {
        _cache.IsStale("repo").Should().BeTrue();

        ServeIndex(RepoLibChunk());
        await _cache.Refresh("repo", CancellationToken.None);
        _cache.IsStale("repo").Should().BeFalse();

        _clock.Now += _settings.RefreshAge - TimeSpan.FromSeconds(1);
        _cache.IsStale("repo").Should().BeFalse();

        _clock.Now += TimeSpan.FromSeconds(2);
        _cache.IsStale("repo").Should().BeTrue();
    }

    [Fact]
    [Trait("RequiresNetworking", "True")]
    public async Task Live_Refresh_ReadsTheRepoIndex()
    {
        var cache = new ThunderstoreIndexCache(new ThunderstoreApi(new HttpClient()), _fileSystem, () => _settings, TimeProvider.System);

        await cache.Refresh("repo", CancellationToken.None);
        var catalog = await cache.Load("repo", CancellationToken.None);

        catalog.TryGet(new ThunderstorePackageRef("BepInEx", "BepInExPack"), out _).Should().BeTrue();
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
