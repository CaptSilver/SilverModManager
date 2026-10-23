using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SilverModManager.Games.Bethesda.LoadOrder;
using SilverModManager.Games.CreationEngine.Abstractions;
using SilverModManager.Games.CreationEngine.SortOrder;
using SilverModManager.Games.CreationEngine.Tests.SkyrimSE;
using SilverModManager.Games.TestFramework;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.IO;
using SilverModManager.StandardGameLocators.TestHelpers;

namespace SilverModManager.Games.CreationEngine.Tests.SortOrder;

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

        await cache.WarmAsync([(a, "a.esp"), (a, "a.esp"), (b, "b.esp")]);
        await cache.WarmAsync([(b, "b.esp"), (a, "a.esp")]);

        await game.Received(1).ParsePlugin(a, Arg.Any<RelativePath?>());
        await game.Received(1).ParsePlugin(b, Arg.Any<RelativePath?>());
        cache.TryGet(a, "a.esp", out var header).Should().BeTrue();
        header!.FileName.Should().Be("a.esp");
    }

    [Fact]
    public void TryGet_AHashThatWasNeverWarmed_IsNotFound()
    {
        var cache = new PluginHeaderCache(Substitute.For<ICreationEngineGame>());

        cache.TryGet(Hash.From(7), "a.esp", out var header).Should().BeFalse();
        header.Should().BeNull();
    }

    [Fact]
    public async Task WarmAsync_ReadsARealPluginHeader()
    {
        var hash = await Store(SkyrimPluginFile.Bytes("Real.esp", "Skyrim.esm"));
        var cache = new PluginHeaderCache(Game);

        await cache.WarmAsync([(hash, "Real.esp")]);

        cache.TryGet(hash, "Real.esp", out var header).Should().BeTrue();
        header!.Masters.Should().Equal("Skyrim.esm");
    }

    [Fact]
    public async Task WarmAsync_UnreadableBytes_RecordsNullWithoutThrowing()
    {
        var garbage = await Store([1, 2, 3, 4, 5, 6, 7, 8]);
        var cache = new PluginHeaderCache(Game);

        var act = async () => await cache.WarmAsync([(garbage, "Bad.esp")]);

        await act.Should().NotThrowAsync();
        cache.TryGet(garbage, "Bad.esp", out var header).Should().BeTrue("the hash was tried, so it is not tried again");
        header.Should().BeNull();
    }

    [Fact]
    public async Task WarmAsync_BytesNoStoreHas_RecordsNull()
    {
        var cache = new PluginHeaderCache(Game);
        var gone = Hash.From(0xDEAD);

        await cache.WarmAsync([(gone, "Gone.esp")]);

        cache.TryGet(gone, "Gone.esp", out var header).Should().BeTrue();
        header.Should().BeNull();
    }

    [Fact]
    public async Task WarmAsync_HandsTheGameTheFileNameOfThePlugin()
    {
        var game = Substitute.For<ICreationEngineGame>();
        var hash = Hash.From(3);
        game.ParsePlugin(hash, Arg.Any<RelativePath?>()).Returns(ValueTask.FromResult<PluginHeader?>(HeaderFor("x")));
        var cache = new PluginHeaderCache(game);

        await cache.WarmAsync([(hash, "Scripts.omwscripts")]);

        await game.Received(1).ParsePlugin(hash, RelativePath.FromUnsanitizedInput("Scripts.omwscripts"));
    }

    [Fact]
    public async Task WarmAsync_SameBytesUnderDifferentExtensions_AreParsedAndCachedSeparately()
    {
        var game = Substitute.For<ICreationEngineGame>();
        var hash = Hash.From(4);
        game.ParsePlugin(hash, Arg.Any<RelativePath?>()).Returns(call =>
            ValueTask.FromResult<PluginHeader?>(HeaderFor(call.Arg<RelativePath?>()!.Value.FileName.ToString())));
        var cache = new PluginHeaderCache(game);

        await cache.WarmAsync([(hash, "A.esp"), (hash, "A.omwscripts"), (hash, "B.ESP")]);

        await game.Received(2).ParsePlugin(hash, Arg.Any<RelativePath?>());
        cache.TryGet(hash, "A.omwscripts", out var script).Should().BeTrue();
        script!.FileName.Should().Be("A.omwscripts");
        cache.TryGet(hash, "Other.esp", out var plugin).Should().BeTrue("the key is the extension, not the whole name");
        plugin!.FileName.Should().Be("A.esp");
    }

    [Fact]
    public async Task WarmAsync_ConcurrentCallsForTheSameHash_ParseItOnce()
    {
        var game = Substitute.For<ICreationEngineGame>();
        var hash = Hash.From(5);
        var parses = 0;
        game.ParsePlugin(hash, Arg.Any<RelativePath?>()).Returns(_ =>
        {
            Interlocked.Increment(ref parses);
            return new ValueTask<PluginHeader?>(Task.Delay(50).ContinueWith(_ => (PluginHeader?)HeaderFor("a.esp")));
        });
        var cache = new PluginHeaderCache(game);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(async () => await cache.WarmAsync([(hash, "a.esp")]))));

        parses.Should().Be(1);
    }

    [Fact]
    public async Task WarmAsync_AParseThatFails_IsTriedAgainOnTheNextWarm()
    {
        var game = Substitute.For<ICreationEngineGame>();
        var hash = Hash.From(6);
        var calls = 0;
        game.ParsePlugin(hash, Arg.Any<RelativePath?>()).Returns(_ => ++calls == 1
            ? ValueTask.FromException<PluginHeader?>(new IOException("store unavailable"))
            : ValueTask.FromResult<PluginHeader?>(HeaderFor("a.esp")));
        var cache = new PluginHeaderCache(game);

        var first = async () => await cache.WarmAsync([(hash, "a.esp")]);
        await first.Should().ThrowAsync<IOException>();
        cache.TryGet(hash, "a.esp", out _).Should().BeFalse();

        await cache.WarmAsync([(hash, "a.esp")]);

        cache.TryGet(hash, "a.esp", out var header).Should().BeTrue();
        header!.FileName.Should().Be("a.esp");
    }
}
