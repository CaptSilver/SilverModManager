using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Games.CreationEngine.Abstractions;
using SilverModManager.Games.TestFramework;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.StandardGameLocators.TestHelpers;
using Xunit;

namespace SilverModManager.Games.CreationEngine.Tests.Fallout4;

public class ParsePluginTests(ITestOutputHelper helper) : AIsolatedGameTest<ParsePluginTests, CreationEngine.Fallout4.Fallout4>(helper)
{
    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddCreationEngine()
            .AddUniversalGameLocator<CreationEngine.Fallout4.Fallout4>(new Version("1.10.163"), locationsFileSystem: IsolatedKnownFolders.Create);
    }

    [Fact]
    public async Task ParsePlugin_NoStoreHasTheBytes_ReturnsNull()
    {
        var header = await ((ICreationEngineGame)Game).ParsePlugin(Hash.From(0xDEAD), RelativePath.FromUnsanitizedInput("Gone.esp"));

        header.Should().BeNull();
    }
}
