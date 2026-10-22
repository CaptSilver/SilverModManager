using Microsoft.Extensions.DependencyInjection;
using NexusMods.Games.TestFramework;
using NexusMods.Sdk.Games;
using NexusMods.StandardGameLocators.TestHelpers;

namespace NexusMods.Games.Unity.Tests.Fixtures;

/// <summary>Runs a test against one of the real Unity games, registered the way the app registers them.</summary>
public abstract class AUnityGameTests<TTest, TGame>(ITestOutputHelper outputHelper) : AIsolatedGameTest<TTest, TGame>(outputHelper)
    where TGame : AUnityGame<TGame>, IGameData<TGame>
{
    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddUnityGames()
            .AddUniversalGameLocator<TGame>(new Version("1.0"));
    }
}
