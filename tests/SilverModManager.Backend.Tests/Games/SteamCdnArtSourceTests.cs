using System.Collections.Immutable;
using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NexusMods.Paths;
using SilverModManager.Backend.Games;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.IO;

namespace SilverModManager.Backend.Tests.Games;

public class SteamCdnArtSourceTests
{
    private const string Cover = "https://cdn.cloudflare.steamstatic.com/steam/apps/413150/library_600x900.jpg";
    private const string Header = "https://cdn.cloudflare.steamstatic.com/steam/apps/413150/header.jpg";

    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3];
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 9];

    private readonly InMemoryFileSystem _fileSystem = new(OSInformation.FakeUnix);
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeSteamImageServer _server = new();

    private AbsolutePath CacheRoot => _fileSystem.FromUnsanitizedFullPath("/cache/art");

    private SteamCdnArtSource CreateSource(TimeSpan? timeout = null, int maxConcurrency = 4) =>
        new(() => CacheRoot, new HttpClient(_server), NullLogger<SteamCdnArtSource>.Instance, _clock, timeout ?? TimeSpan.FromSeconds(5), maxConcurrency);

    private static GameArtRequest Request(params uint[] ids) => new(GameId.From("Test.Game"), "Test Game", [..ids]);

    private static async Task<byte[]> ReadAll(IStreamFactory? factory)
    {
        factory.Should().NotBeNull();
        await using var stream = await factory!.GetStreamAsync();
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory);
        return memory.ToArray();
    }

    [Fact]
    public async Task Tile_ServerHasTheCover_ReturnsItAndWritesTheCache()
    {
        _server.Serve(Cover, Jpeg);

        var tile = await CreateSource().FindTileAsync(Request(413150));

        (await ReadAll(tile)).Should().Equal(Jpeg);
        CacheRoot.EnumerateFiles().Should().ContainSingle(f => f.FileName == "library_600x900.jpg");
    }

    [Fact]
    public async Task Tile_ServedAPng_IsAccepted()
    {
        _server.Serve(Cover, Png);

        (await ReadAll(await CreateSource().FindTileAsync(Request(413150)))).Should().Equal(Png);
    }

    [Fact]
    public async Task Tile_ReadAgain_IsServedFromTheCacheWithoutARequest()
    {
        _server.Serve(Cover, Jpeg);
        var source = CreateSource();
        await source.FindTileAsync(Request(413150));
        _server.Requests.Clear();

        // A new instance too: the cache is on disk, not in the object.
        var again = await CreateSource().FindTileAsync(Request(413150));

        (await ReadAll(again)).Should().Equal(Jpeg);
        _server.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Tile_CoverIs404_FallsBackToTheHeader()
    {
        _server.Serve(Header, Jpeg);

        var tile = await CreateSource().FindTileAsync(Request(413150));

        (await ReadAll(tile)).Should().Equal(Jpeg);
        _server.Requests.Should().Equal(Cover, Header);
    }

    [Fact]
    public async Task Tile_EverythingIs404_ReturnsNullAndIsNotAskedAgainUntilTheMarkerExpires()
    {
        var source = CreateSource();

        (await source.FindTileAsync(Request(413150))).Should().BeNull();
        _server.Requests.Should().Equal(Cover, Header);

        _server.Requests.Clear();
        _clock.Advance(TimeSpan.FromDays(6));
        (await CreateSource().FindTileAsync(Request(413150))).Should().BeNull();
        _server.Requests.Should().BeEmpty("a missing asset is remembered across starts");

        _clock.Advance(TimeSpan.FromDays(2));
        (await CreateSource().FindTileAsync(Request(413150))).Should().BeNull();
        _server.Requests.Should().Equal(Cover, Header);
    }

    [Fact]
    public async Task Tile_EndlessBodyWithNoLength_StopsReadingAtTheSizeLimit()
    {
        var body = new EndlessImageStream(Jpeg);
        _server.ServeStream(Cover, body);

        var tile = await CreateSource().FindTileAsync(Request(413150));

        tile.Should().BeNull();
        body.BytesRead.Should().BeLessThan(9L * 1024 * 1024, "the download must stop once it passes the 8 MiB limit, not read the whole body");
    }

    [Fact]
    public async Task Tile_NonImageBody_IsRejectedAndNotCached()
    {
        _server.Serve(Cover, "<html>captive portal</html>"u8.ToArray());
        _server.Serve(Header, "<html>captive portal</html>"u8.ToArray());

        (await CreateSource().FindTileAsync(Request(413150))).Should().BeNull();

        CacheRoot.DirectoryExists().Should().BeFalse("nothing but a validated image may reach the cache");
    }

    [Fact]
    public async Task Tile_ServerError_ReturnsNullWithoutAMarkerAndIsNotRetriedImmediately()
    {
        _server.Fail(Cover, HttpStatusCode.ServiceUnavailable);
        var source = CreateSource();

        (await source.FindTileAsync(Request(413150))).Should().BeNull();
        var requests = _server.Requests.Count;
        (await source.FindTileAsync(Request(413150))).Should().BeNull();

        _server.Requests.Count.Should().Be(requests, "a failing server is left alone for a few minutes");
        _clock.Advance(TimeSpan.FromMinutes(10));
        _server.Serve(Cover, Jpeg);
        (await ReadAll(await source.FindTileAsync(Request(413150)))).Should().Equal(Jpeg);
    }

    [Fact]
    public async Task Tile_CacheFolderIsReadOnly_ReturnsNullAndIsNotDownloadedAgainImmediately()
    {
        if (Environment.IsPrivilegedProcess) return; // root ignores permission bits

        var root = Directory.CreateTempSubdirectory("sdm-art-ro-");
        try
        {
            File.SetUnixFileMode(root.FullName, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            _server.Serve(Cover, Jpeg);
            var source = new SteamCdnArtSource(() => FileSystem.Shared.FromUnsanitizedFullPath(root.FullName), new HttpClient(_server), NullLogger<SteamCdnArtSource>.Instance, _clock, TimeSpan.FromSeconds(5), 4);

            (await source.FindTileAsync(Request(413150))).Should().BeNull();
            var requests = _server.Requests.Count;
            (await source.FindTileAsync(Request(413150))).Should().BeNull();

            _server.Requests.Count.Should().Be(requests, "an unwritable cache must back off like any other failure");
        }
        finally
        {
            File.SetUnixFileMode(root.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Tile_CachedFileIsUnreadable_DoesNotThrow()
    {
        if (Environment.IsPrivilegedProcess) return; // root ignores permission bits

        var root = Directory.CreateTempSubdirectory("sdm-art-unreadable-");
        try
        {
            var file = Path.Combine(root.FullName, "413150", "library_600x900.jpg");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, Jpeg);
            File.SetUnixFileMode(file, UnixFileMode.None);
            _server.Serve(Cover, Jpeg);
            var source = new SteamCdnArtSource(() => FileSystem.Shared.FromUnsanitizedFullPath(root.FullName), new HttpClient(_server), NullLogger<SteamCdnArtSource>.Instance, _clock, TimeSpan.FromSeconds(5), 4);

            var act = async () => await source.FindTileAsync(Request(413150));

            await act.Should().NotThrowAsync();
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Tile_ServerNeverAnswers_GivesUpAfterTheTimeout()
    {
        _server.Hang(Cover);

        var tile = await CreateSource(timeout: TimeSpan.FromMilliseconds(100)).FindTileAsync(Request(413150));

        tile.Should().BeNull();
    }

    [Fact]
    public async Task Tile_ConnectionFails_ReturnsNull()
    {
        _server.Throw(Cover, new HttpRequestException("no route to host"));

        (await CreateSource().FindTileAsync(Request(413150))).Should().BeNull();
    }

    [Fact]
    public async Task Tile_OnlyTheSecondIdHasArt_UsesTheSecond()
    {
        _server.Serve("https://cdn.cloudflare.steamstatic.com/steam/apps/2/library_600x900.jpg", Jpeg);

        var tile = await CreateSource().FindTileAsync(Request(1, 2));

        (await ReadAll(tile)).Should().Equal(Jpeg);
    }

    [Fact]
    public async Task Tile_NoSteamId_NeverAsksTheServer()
    {
        (await CreateSource().FindTileAsync(Request())).Should().BeNull();

        _server.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Icon_UsesTheCoverAndNeverTheLandscapeHeader()
    {
        _server.Serve(Header, Jpeg);

        (await CreateSource().FindIconAsync(Request(413150))).Should().BeNull();

        _server.Requests.Should().Equal(Cover);
    }

    [Fact]
    public async Task Icon_ServerHasTheCover_ReturnsIt()
    {
        _server.Serve(Cover, Jpeg);

        (await ReadAll(await CreateSource().FindIconAsync(Request(413150)))).Should().Equal(Jpeg);
    }

    [Fact]
    public async Task ConcurrentLookups_NeverExceedTheConcurrencyCap()
    {
        var ids = Enumerable.Range(100, 12).Select(i => (uint)i).ToArray();
        foreach (var id in ids) _server.Hold($"https://cdn.cloudflare.steamstatic.com/steam/apps/{id}/library_600x900.jpg", Jpeg);

        // The downloads write their cache folders at the same moment, which the in-memory file system can't take.
        var root = Directory.CreateTempSubdirectory("sdm-art-concurrent-");
        try
        {
            var source = new SteamCdnArtSource(() => FileSystem.Shared.FromUnsanitizedFullPath(root.FullName), new HttpClient(_server), NullLogger<SteamCdnArtSource>.Instance, _clock, TimeSpan.FromSeconds(5), 3);

            var lookups = ids.Select(id => source.FindTileAsync(Request(id)).AsTask()).ToArray();
            await _server.WaitUntilRequests(3);
            await Task.Delay(100);
            _server.MaxInFlight.Should().Be(3);

            _server.ReleaseAll();
            var results = await Task.WhenAll(lookups);

            results.Should().OnlyContain(r => r != null);
            _server.MaxInFlight.Should().Be(3);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task SameAssetRequestedTwiceAtOnce_IsDownloadedOnce()
    {
        _server.Hold(Cover, Jpeg);
        var source = CreateSource();

        var first = source.FindTileAsync(Request(413150)).AsTask();
        var second = source.FindTileAsync(Request(413150)).AsTask();
        await _server.WaitUntilRequests(1);
        _server.ReleaseAll();
        await Task.WhenAll(first, second);

        _server.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Tile_CacheLeftBehindByACrashedWrite_IsIgnored()
    {
        var folder = CacheRoot.Combine("413150");
        folder.CreateDirectory();
        await folder.Combine("library_600x900.jpg.tmp-abc").WriteAllBytesAsync([1, 2, 3]);
        _server.Serve(Cover, Jpeg);

        (await ReadAll(await CreateSource().FindTileAsync(Request(413150)))).Should().Equal(Jpeg);
    }

    [Fact]
    public async Task Tile_CachedFileIsNotAnImage_IsReplacedByADownload()
    {
        var folder = CacheRoot.Combine("413150");
        folder.CreateDirectory();
        await folder.Combine("library_600x900.jpg").WriteAllBytesAsync([1, 2, 3]);
        _server.Serve(Cover, Jpeg);

        (await ReadAll(await CreateSource().FindTileAsync(Request(413150)))).Should().Equal(Jpeg);
    }
}

/// <summary>An <see cref="HttpMessageHandler"/> that plays Steam's image server; every URL not served is a 404.</summary>
internal sealed class FakeSteamImageServer : HttpMessageHandler
{
    private readonly Dictionary<string, Func<CancellationToken, Task<HttpResponseMessage>>> _routes = new();
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _inFlight;
    private int _maxInFlight;

    public List<string> Requests { get; } = [];

    public int MaxInFlight => _maxInFlight;

    public void Serve(string url, byte[] body) => _routes[url] = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });

    /// <summary>Serves a body with no Content-Length, as a chunked response would arrive.</summary>
    public void ServeStream(string url, Stream body) => _routes[url] = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) });

    public void Fail(string url, HttpStatusCode status) => _routes[url] = _ => Task.FromResult(new HttpResponseMessage(status));

    public void Throw(string url, Exception exception) => _routes[url] = _ => Task.FromException<HttpResponseMessage>(exception);

    public void Hang(string url) => _routes[url] = async ct =>
    {
        await Task.Delay(Timeout.Infinite, ct);
        return new HttpResponseMessage(HttpStatusCode.OK);
    };

    /// <summary>Answers only after <see cref="ReleaseAll"/>.</summary>
    public void Hold(string url, byte[] body) => _routes[url] = async _ =>
    {
        await _release.Task;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
    };

    public void ReleaseAll() => _release.TrySetResult();

    public async Task WaitUntilRequests(int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            lock (Requests) if (Requests.Count >= count) return;
            await Task.Delay(10);
        }

        throw new TimeoutException($"Fewer than {count} requests arrived");
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
        lock (Requests) Requests.Add(url);

        var now = Interlocked.Increment(ref _inFlight);
        int seen;
        while ((seen = _maxInFlight) < now && Interlocked.CompareExchange(ref _maxInFlight, now, seen) != seen) { }

        try
        {
            return _routes.TryGetValue(url, out var route)
                ? await route(cancellationToken)
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }
}

/// <summary>A body that starts like an image and never ends, counting what the reader took from it.</summary>
internal sealed class EndlessImageStream(byte[] start) : Stream
{
    private long _position;

    public long BytesRead => Interlocked.Read(ref _position);

    public override int Read(byte[] buffer, int offset, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var at = _position + i;
            buffer[offset + i] = at < start.Length ? start[at] : (byte)0;
        }

        Interlocked.Add(ref _position, count);
        return count;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _position; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
