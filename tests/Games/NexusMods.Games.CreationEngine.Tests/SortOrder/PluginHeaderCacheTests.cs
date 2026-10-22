using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NexusMods.Games.Bethesda.LoadOrder;
using NexusMods.Games.CreationEngine.Abstractions;
using NexusMods.Games.CreationEngine.SortOrder;
using NexusMods.Games.CreationEngine.Tests.SkyrimSE;
using NexusMods.Games.TestFramework;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using NexusMods.Sdk.FileStore;
using NexusMods.Sdk.IO;
using NexusMods.StandardGameLocators.TestHelpers;

namespace NexusMods.Games.CreationEngine.Tests.SortOrder;

public class PluginHeaderCacheTests(ITestOutputHelper helper)
    : AIsolatedGameTest<PluginHeaderCacheTests, CreationEngine.SkyrimSE.SkyrimSE>(helper)
{
    protected override IServiceCollection AddServices(IServiceCollection services) => base.AddServices(services)
        .AddCreationEngine()
        .AddUniversalGameLocator<CreationEngine.SkyrimSE.SkyrimSE>(new Version("1.6.1170"), locationsFileSystem: IsolatedKnownFolders.Create);

    private static PluginHeader HeaderFor(string name) => new(name, [], 0, 1.7f, 0, null, null, PluginFormat.Tes4);

    private async Task<Hash> Store(byte[] bytes)
    {
        var hash = bytes.xxHash3();
        await FileStore.BackupFiles([new ArchivedFileEntry(new MemoryStreamFactory("plugin", new MemoryStream(bytes)), hash, Size.FromLong(bytes.Length))]);
        return hash;
    }

    [Fact]
    public async Task WarmAsync_ParsesEachHashOnce()
    {
        var game = Substitute.For<ICreationEngineGame>();
        var (a, b) = (Hash.From(1), Hash.From(2));
        // Hash has no usable default, so the substitute is told about each hash instead of matching any.
        game.ParsePlugin(a, Arg.Any<RelativePath?>()).Returns(ValueTask.FromResult<PluginHeader?>(HeaderFor("a.esp")));
        game.ParsePlugin(b, Arg.Any<RelativePath?>()).Returns(ValueTask.FromResult<PluginHeader?>(HeaderFor("b.esp")));
        var cache = new PluginHeaderCache(game);

        await cache.WarmAsync([a, a, b]);
        await cache.WarmAsync([b, a]);

        await game.Received(1).ParsePlugin(a, Arg.Any<RelativePath?>());
        await game.Received(1).ParsePlugin(b, Arg.Any<RelativePath?>());
        cache.TryGet(a, out var header).Should().BeTrue();
        header!.FileName.Should().Be("a.esp");
    }

    [Fact]
    public void TryGet_AHashThatWasNeverWarmed_IsNotFound()
    {
        var cache = new PluginHeaderCache(Substitute.For<ICreationEngineGame>());

        cache.TryGet(Hash.From(7), out var header).Should().BeFalse();
        header.Should().BeNull();
    }

    [Fact]
    public async Task WarmAsync_ReadsARealPluginHeader()
    {
        var hash = await Store(SkyrimPluginFile.Bytes("Real.esp", "Skyrim.esm"));
        var cache = new PluginHeaderCache(Game);

        await cache.WarmAsync([hash]);

        cache.TryGet(hash, out var header).Should().BeTrue();
        header!.Masters.Should().Equal("Skyrim.esm");
    }

    [Fact]
    public async Task WarmAsync_UnreadableBytes_RecordsNullWithoutThrowing()
    {
        var garbage = await Store([1, 2, 3, 4, 5, 6, 7, 8]);
        var cache = new PluginHeaderCache(Game);

        var act = async () => await cache.WarmAsync([garbage]);

        await act.Should().NotThrowAsync();
        cache.TryGet(garbage, out var header).Should().BeTrue("the hash was tried, so it is not tried again");
        header.Should().BeNull();
    }

    [Fact]
    public async Task WarmAsync_BytesNoStoreHas_RecordsNull()
    {
        var cache = new PluginHeaderCache(Game);
        var gone = Hash.From(0xDEAD);

        await cache.WarmAsync([gone]);

        cache.TryGet(gone, out var header).Should().BeTrue();
        header.Should().BeNull();
    }
}
