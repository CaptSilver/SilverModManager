using System.Collections.Immutable;
using FluentAssertions;
using SilverModManager.Backend.Games;
using SilverModManager.Sdk.Games;
using NexusMods.Paths;
using SilverModManager.Sdk.IO;

namespace SilverModManager.Backend.Tests.Games;

public class SteamLibraryCacheArtSourceTests
{
    private const string TileFile = "library_600x900.jpg";
    private const string ClientIconFile = "3219f537a98507daa570184631c2b2a1a50e71f3.jpg";

    // Newer Steam clients put each asset in its own hash-named folder under the app folder.
    private const string AssetFolder = "6399de67cce3cecadce7900e03f6e09ff910a235";

    private static readonly byte[] TileBytes = [1, 2, 3, 4];
    private static readonly byte[] IconBytes = [5, 6, 7];

    private readonly InMemoryFileSystem _fileSystem = new(OSInformation.FakeUnix);

    private AbsolutePath Root => _fileSystem.FromUnsanitizedFullPath("/steam");

    private static Probe CreateSource(params AbsolutePath[] roots) => new(new SteamLibraryCacheArtSource(() => roots));

    /// <summary>Asks the source the way the chain does, naming a game that nobody else cares about.</summary>
    private sealed class Probe(SteamLibraryCacheArtSource source)
    {
        private static GameArtRequest Request(ImmutableArray<uint> ids) => new(GameId.From("Test.Game"), "Test Game", ids);

        public Task<IStreamFactory?> Tile(ImmutableArray<uint> ids) => source.FindTileAsync(Request(ids)).AsTask();

        public Task<IStreamFactory?> Icon(ImmutableArray<uint> ids) => source.FindIconAsync(Request(ids)).AsTask();
    }

    private async Task WriteCacheFile(uint appId, string fileName, byte[] bytes, AbsolutePath? root = null)
    {
        var file = (root ?? Root).Combine("appcache/librarycache").Combine(appId.ToString()).Combine(fileName);
        file.Parent.CreateDirectory();
        await file.WriteAllBytesAsync(bytes);
    }

    private static async Task<byte[]> ReadAll(Task<IStreamFactory?> lookup)
    {
        var factory = await lookup;
        factory.Should().NotBeNull();
        await using var stream = await factory!.GetStreamAsync();
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory);
        return memory.ToArray();
    }

    [Fact]
    public async Task Tile_CachedCover_StreamsTheCachedFile()
    {
        await WriteCacheFile(413150, TileFile, TileBytes);

        var bytes = await ReadAll(CreateSource(Root).Tile([413150]));

        bytes.Should().Equal(TileBytes);
    }

    [Fact]
    public async Task Tile_NoCacheFolder_HasNoAnswer()
    {
        (await CreateSource(Root).Tile([413150])).Should().BeNull();
    }

    [Fact]
    public async Task Tile_OnlyTheSecondIdHasArt_UsesTheSecond()
    {
        await WriteCacheFile(2, TileFile, TileBytes);

        var bytes = await ReadAll(CreateSource(Root).Tile([1, 2]));

        bytes.Should().Equal(TileBytes);
    }

    [Fact]
    public async Task Tile_BothIdsHaveArt_UsesTheFirst()
    {
        await WriteCacheFile(1, TileFile, TileBytes);
        await WriteCacheFile(2, TileFile, IconBytes);

        var bytes = await ReadAll(CreateSource(Root).Tile([1, 2]));

        bytes.Should().Equal(TileBytes);
    }

    [Fact]
    public async Task Tile_NoSteamRoot_HasNoAnswer()
    {
        (await CreateSource().Tile([413150])).Should().BeNull();
    }

    [Fact]
    public async Task Tile_NoAppIds_HasNoAnswer()
    {
        (await CreateSource(Root).Tile([])).Should().BeNull();
    }

    [Fact]
    public async Task Tile_ArtInTheSecondRoot_IsFound()
    {
        var second = _fileSystem.FromUnsanitizedFullPath("/other-steam");
        await WriteCacheFile(413150, TileFile, TileBytes, second);

        var bytes = await ReadAll(CreateSource(Root, second).Tile([413150]));

        bytes.Should().Equal(TileBytes);
    }

    [Fact]
    public async Task Tile_CoverInAHashSubfolder_IsFound()
    {
        await WriteCacheFile(1091500, $"{AssetFolder}/{TileFile}", TileBytes);

        var bytes = await ReadAll(CreateSource(Root).Tile([1091500]));

        bytes.Should().Equal(TileBytes);
    }

    [Fact]
    public async Task Tile_OnlyACapsuleInAHashSubfolder_UsesTheCapsule()
    {
        await WriteCacheFile(1091500, $"{AssetFolder}/library_capsule.jpg", TileBytes);
        await WriteCacheFile(1091500, "other/library_header.jpg", [9]);

        var bytes = await ReadAll(CreateSource(Root).Tile([1091500]));

        bytes.Should().Equal(TileBytes);
    }

    [Fact]
    public async Task Tile_CoverAndCapsule_PrefersTheCover()
    {
        await WriteCacheFile(1091500, $"{AssetFolder}/library_capsule.jpg", IconBytes);
        await WriteCacheFile(1091500, TileFile, TileBytes);

        var bytes = await ReadAll(CreateSource(Root).Tile([1091500]));

        bytes.Should().Equal(TileBytes);
    }

    [Fact]
    public async Task Icon_LibraryHeaderNextToTheClientIcon_StillPicksTheClientIcon()
    {
        await WriteCacheFile(377160, ClientIconFile, IconBytes);
        await WriteCacheFile(377160, TileFile, TileBytes);
        await WriteCacheFile(377160, "library_header.jpg", [9]);
        await WriteCacheFile(377160, "library_capsule.jpg", [9]);

        var bytes = await ReadAll(CreateSource(Root).Icon([377160]));

        bytes.Should().Equal(IconBytes);
    }

    [Fact]
    public async Task Icon_ClientIconPresent_StreamsTheHashNamedFile()
    {
        await WriteCacheFile(413150, ClientIconFile, IconBytes);
        await WriteCacheFile(413150, TileFile, TileBytes);
        await WriteCacheFile(413150, "header.jpg", [9]);
        await WriteCacheFile(413150, "library_hero.jpg", [9]);
        await WriteCacheFile(413150, "library_hero_blur.jpg", [9]);

        var bytes = await ReadAll(CreateSource(Root).Icon([413150]));

        bytes.Should().Equal(IconBytes);
    }

    [Fact]
    public async Task Icon_NoClientIcon_FallsBackToTheCover()
    {
        await WriteCacheFile(413150, TileFile, TileBytes);
        await WriteCacheFile(413150, "header.jpg", [9]);

        var bytes = await ReadAll(CreateSource(Root).Icon([413150]));

        bytes.Should().Equal(TileBytes);
    }

    [Fact]
    public async Task Icon_NothingCached_HasNoAnswer()
    {
        (await CreateSource(Root).Icon([413150])).Should().BeNull();
    }

    [Fact]
    public async Task Icon_ClientIconOnlyInTheSecondId_IsPreferredOverTheFirstIdsCover()
    {
        await WriteCacheFile(1, TileFile, TileBytes);
        await WriteCacheFile(2, ClientIconFile, IconBytes);

        var bytes = await ReadAll(CreateSource(Root).Icon([1, 2]));

        bytes.Should().Equal(IconBytes);
    }
}
