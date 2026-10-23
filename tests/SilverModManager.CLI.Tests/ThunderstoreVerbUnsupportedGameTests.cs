using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Games;
using NexusMods.Games.TestFramework;
using NexusMods.Networking.Thunderstore.Tests.Fixtures;
using NexusMods.StandardGameLocators.TestHelpers;
using NexusMods.StandardGameLocators.TestHelpers.StubbedGames;

namespace NexusMods.CLI.Tests;

/// <summary>
/// The <c>thunderstore</c> verbs against a loadout whose game takes its mods from somewhere else.
/// </summary>
[Collection("ConsoleOutput")]
public class ThunderstoreVerbUnsupportedGameTests(ITestOutputHelper helper) : AIsolatedGameTest<ThunderstoreVerbUnsupportedGameTests, NoNexusIdStubbedGame>(helper)
{
    private readonly RoutingHandler _routes = ThunderstoreVerbFixtures.BuildRoutes();

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        base.AddServices(services)
            .AddGame<NoNexusIdStubbedGame>()
            .AddUniversalGameLocator<NoNexusIdStubbedGame>(Version.Parse("0.0.0"))
            .AddVerbHost(_routes);
        return services;
    }

    [Theory]
    [InlineData("refresh")]
    [InlineData("search --query repolib")]
    [InlineData("install --package Zehs-REPOLib")]
    [InlineData("updates")]
    public async Task VerbExitsWithOneNamingTheGame(string verb)
    {
        var loadout = await CreateLoadout();

        var (code, output) = await ThunderstoreVerbFixtures.Run(ServiceProvider, ["thunderstore", .. verb.Split(' '), "--loadout", loadout.Name]);

        code.Should().Be(1, output);
        output.Should().Contain(NoNexusIdStubbedGame.DisplayName);
        _routes.Requests.Should().BeEmpty();
    }
}
