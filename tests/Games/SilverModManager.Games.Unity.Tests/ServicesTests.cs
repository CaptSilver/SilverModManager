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
    public void EachGameHasAToolToRunIt()
    {
        var tools = ServiceProvider.GetServices<ITool>().ToArray();

        tools.OfType<RunGameTool<Unity.Repo.Repo>>().Should().ContainSingle();
        tools.OfType<RunGameTool<Unity.Peak.Peak>>().Should().ContainSingle();
    }
}
