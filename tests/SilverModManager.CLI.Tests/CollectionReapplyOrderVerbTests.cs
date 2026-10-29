using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.TestFramework;
using SilverModManager.Networking.Thunderstore.Tests.Fixtures;
using SilverModManager.Abstractions.Games;
using SilverModManager.Sdk;
using SilverModManager.StandardGameLocators.TestHelpers;
using SilverModManager.StandardGameLocators.TestHelpers.StubbedGames;

namespace SilverModManager.CLI.Tests;

/// <summary><c>collection reapply-order</c>: it answers on the command line and never reaches for a dialog.</summary>
[Collection("ConsoleOutput")]
public class CollectionReapplyOrderVerbTests(ITestOutputHelper helper) : AIsolatedGameTest<CollectionReapplyOrderVerbTests, ThunderstoreStubbedGame>(helper)
{
    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        base.AddServices(services)
            .AddGame<ThunderstoreStubbedGame>()
            .AddUniversalGameLocator<ThunderstoreStubbedGame>(Version.Parse("0.0.0"))
            .AddVerbHost(ThunderstoreVerbFixtures.BuildRoutes());
        return services;
    }

    [Fact]
    public async Task ACollectionTheLoadoutDoesNotHaveIsReportedAndExitsOne()
    {
        var loadout = await CreateLoadout();

        var (code, output) = await ThunderstoreVerbFixtures.Run(ServiceProvider, "collection", "reapply-order", "--loadout", loadout.Name, "--collection", "no-such-collection");

        code.Should().Be(1, output);
        output.Should().Contain("no collection `no-such-collection`");
    }

    [Fact]
    public async Task TheVerbIsListedUnderCollection()
    {
        var (_, output) = await ThunderstoreVerbFixtures.Run(ServiceProvider, "collection", "reapply-order", "--help");

        output.Should().Contain("reapply-order");
    }
}
