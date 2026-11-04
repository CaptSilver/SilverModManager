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

public class GameArtChainTests
{
    private static readonly byte[] LocalBytes = [0xFF, 0xD8, 0xFF, 1];
    private static readonly byte[] CdnBytes = [0xFF, 0xD8, 0xFF, 2];

    private readonly InMemoryFileSystem _fileSystem = new(OSInformation.FakeUnix);
    private readonly FakeSteamImageServer _server = new();

    private AbsolutePath Steam => _fileSystem.FromUnsanitizedFullPath("/steam");

    private IGameArtSource CreateChain(AbsolutePath[]? steamRoots = null)
    {
        var clock = new FakeTimeProvider();
        return new GameArtChain(
            [
                new SteamLibraryCacheArtSource(() => steamRoots ?? [Steam]),
                new SteamCdnArtSource(() => _fileSystem.FromUnsanitizedFullPath("/cache/art"), new HttpClient(_server), NullLogger<SteamCdnArtSource>.Instance, clock, TimeSpan.FromSeconds(5), 4),
                new InitialsArtSource(),
            ],
            NullLogger<GameArtChain>.Instance);
    }

    private static GameArtRequest Request(params uint[] ids) => new(GameId.From("Test.Game"), "Test Game", [..ids]);

    private static async Task<byte[]> ReadAll(IStreamFactory factory)
    {
        await using var stream = await factory.GetStreamAsync();
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory);
        return memory.ToArray();
    }

    private async Task WriteSteamCover(uint appId, byte[] bytes)
    {
        var file = Steam.Combine("appcache/librarycache").Combine(appId.ToString()).Combine("library_600x900.jpg");
        file.Parent.CreateDirectory();
        await file.WriteAllBytesAsync(bytes);
    }

    private static bool IsPng(byte[] bytes) => bytes.Length > 8 && bytes[..4].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47 });

    [Fact]
    public async Task Tile_SteamHasItLocally_BeatsTheServer()
    {
        await WriteSteamCover(413150, LocalBytes);
        _server.Serve("https://cdn.cloudflare.steamstatic.com/steam/apps/413150/library_600x900.jpg", CdnBytes);

        var bytes = await ReadAll(CreateChain().Tile(Request(413150)));

        bytes.Should().Equal(LocalBytes);
        _server.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Tile_NotCachedLocally_ComesFromTheServer()
    {
        _server.Serve("https://cdn.cloudflare.steamstatic.com/steam/apps/413150/library_600x900.jpg", CdnBytes);

        var bytes = await ReadAll(CreateChain().Tile(Request(413150)));

        bytes.Should().Equal(CdnBytes);
    }

    [Fact]
    public async Task Tile_NeitherHasIt_IsTheInitialsPicture()
    {
        var bytes = await ReadAll(CreateChain().Tile(Request(413150)));

        IsPng(bytes).Should().BeTrue();
    }

    [Fact]
    public async Task Tile_TheServerTimesOut_IsTheInitialsPicture()
    {
        _server.Hang("https://cdn.cloudflare.steamstatic.com/steam/apps/413150/library_600x900.jpg");
        var chain = new GameArtChain(
            [
                new SteamLibraryCacheArtSource(() => []),
                new SteamCdnArtSource(() => _fileSystem.FromUnsanitizedFullPath("/cache/art"), new HttpClient(_server), NullLogger<SteamCdnArtSource>.Instance, new FakeTimeProvider(), TimeSpan.FromMilliseconds(100), 4),
                new InitialsArtSource(),
            ],
            NullLogger<GameArtChain>.Instance);

        IsPng(await ReadAll(chain.Tile(Request(413150)))).Should().BeTrue();
    }

    [Fact]
    public async Task Tile_NoSteamId_IsTheInitialsPictureWithoutAnyRequest()
    {
        IsPng(await ReadAll(CreateChain().Tile(Request()))).Should().BeTrue();

        _server.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Icon_NoSteamId_IsTheInitialsPicture()
    {
        IsPng(await ReadAll(CreateChain().Icon(Request()))).Should().BeTrue();
    }

    [Fact]
    public async Task Icon_ClientIconInSteamsCache_BeatsTheServerCover()
    {
        var folder = Steam.Combine("appcache/librarycache/413150");
        folder.CreateDirectory();
        await folder.Combine("3219f537a98507daa570184631c2b2a1a50e71f3.jpg").WriteAllBytesAsync(LocalBytes);
        _server.Serve("https://cdn.cloudflare.steamstatic.com/steam/apps/413150/library_600x900.jpg", CdnBytes);

        (await ReadAll(CreateChain().Icon(Request(413150)))).Should().Equal(LocalBytes);
    }

    [Fact]
    public async Task Tile_ConstructionTouchesNeitherDiskNorNetwork_AndTheLookupIsPerRead()
    {
        var chain = CreateChain();
        var factory = chain.Tile(Request(413150));
        _server.Requests.Should().BeEmpty();

        await WriteSteamCover(413150, LocalBytes);

        (await ReadAll(factory)).Should().Equal(LocalBytes);
    }

    [Fact]
    public async Task Tile_AProviderThrows_TheNextOneAnswers()
    {
        var chain = new GameArtChain([new ThrowingProvider(), new InitialsArtSource()], NullLogger<GameArtChain>.Instance);

        IsPng(await ReadAll(chain.Tile(Request(1)))).Should().BeTrue();
    }

    [Fact]
    public async Task Tile_NoProviderAnswers_IsAnError()
    {
        var chain = new GameArtChain([], NullLogger<GameArtChain>.Instance);

        var act = async () => await chain.Tile(Request(1)).GetStreamAsync();

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Tile_AFoundFileFailsToOpen_TheNextProviderAnswers()
    {
        var chain = new GameArtChain([new UnopenableProvider(), new InitialsArtSource()], NullLogger<GameArtChain>.Instance);

        IsPng(await ReadAll(chain.Tile(Request(1)))).Should().BeTrue();
    }

    [Fact]
    public async Task Icon_AFoundFileFailsToOpen_TheNextProviderAnswers()
    {
        var chain = new GameArtChain([new UnopenableProvider(), new InitialsArtSource()], NullLogger<GameArtChain>.Instance);

        IsPng(await ReadAll(chain.Icon(Request(1)))).Should().BeTrue();
    }

    private sealed class UnopenableProvider : IGameArtProvider
    {
        public ValueTask<IStreamFactory?> FindTileAsync(GameArtRequest request) => new(new BrokenFactory());

        public ValueTask<IStreamFactory?> FindIconAsync(GameArtRequest request) => new(new BrokenFactory());

        private sealed class BrokenFactory : IStreamFactory
        {
            public RelativePath FileName => RelativePath.Empty;

            public ValueTask<Stream> GetStreamAsync() => throw new UnauthorizedAccessException("permission denied");
        }
    }

    private sealed class ThrowingProvider : IGameArtProvider
    {
        public ValueTask<IStreamFactory?> FindTileAsync(GameArtRequest request) => throw new IOException("disk gone");

        public ValueTask<IStreamFactory?> FindIconAsync(GameArtRequest request) => throw new IOException("disk gone");
    }
}
