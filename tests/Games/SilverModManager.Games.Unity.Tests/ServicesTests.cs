using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.Unity.Tests.Fixtures;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.Unity.Tests;

public class ServicesTests(ITestOutputHelper outputHelper) : AUnityInstallerTests<ServicesTests>(outputHelper)
{
    protected override IServiceCollection AddServices(IServiceCollection services)
        => base.AddServices(services).AddUnityGames();

    [Fact]
    public void BothGamesAreRegistered()
    {
        ServiceProvider.GetServices<IGame>().Select(game => game.GameId)
            .Should().Contain([GameId.From("Unity.Repo"), GameId.From("Unity.Peak")]);
    }

    [Fact]
    public void EveryRowOfTheTableIsRegisteredOnce()
    {
        var registered = ServiceProvider.GetServices<IGame>().OfType<UnityGame>().Select(game => game.Definition).ToArray();

        registered.Should().BeEquivalentTo(UnityGames.All);
        registered.Select(definition => definition.GameId).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void EachGameHasAToolToRunIt()
    {
        var tools = ServiceProvider.GetServices<ITool>().ToArray();

        foreach (var definition in UnityGames.All)
            tools.OfType<IRunGameTool>().Where(tool => tool.GameIds.Contains(definition.GameId)).Should().ContainSingle();
    }
}
