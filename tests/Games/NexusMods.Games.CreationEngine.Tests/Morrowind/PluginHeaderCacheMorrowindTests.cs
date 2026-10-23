using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Games;
using NexusMods.Games.CreationEngine.Abstractions;
using NexusMods.Games.CreationEngine.SortOrder;
using NexusMods.Hashing.xxHash3;
using Xunit;

namespace NexusMods.Games.CreationEngine.Tests.Morrowind;

public class PluginHeaderCacheMorrowindTests(IServiceProvider serviceProvider)
{
    private ICreationEngineGame Game => serviceProvider.GetServices<IGame>().OfType<CreationEngine.Morrowind.Morrowind>().Single();

    [Fact]
    public async Task AnOmwscriptsFile_IsCachedAsReadableWithNoMasters_WithoutReachingTheTes3Reader()
    {
        var cache = new PluginHeaderCache(Game);
        // No store holds these bytes, so a read through the TES3 reader would come back null.
        var hash = Hash.From(0xBEEF);

        await cache.WarmAsync([(hash, "Scripts.omwscripts")]);

        cache.TryGet(hash, "Scripts.omwscripts", out var header).Should().BeTrue();
        header.Should().NotBeNull();
        header!.Masters.Should().BeEmpty();
    }

    [Fact]
    public async Task TheSameBytesNamedAsAnEsp_AreNotMistakenForAScriptList()
    {
        var cache = new PluginHeaderCache(Game);
        var hash = Hash.From(0xBEEF);

        await cache.WarmAsync([(hash, "Scripts.omwscripts"), (hash, "Plugin.esp")]);

        cache.TryGet(hash, "Plugin.esp", out var header).Should().BeTrue();
        header.Should().BeNull("the bytes are not in any store, and an esp is read, not assumed");
    }
}
