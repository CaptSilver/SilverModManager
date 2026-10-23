using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.StandardGameLocators.TestHelpers;
using SilverModManager.StandardGameLocators.TestHelpers.StubbedGames;
using Xunit;

namespace SilverModManager.Games.TestFramework;

/// <summary>
/// Base for tests that need the stubbed game: it has Game, Preferences and Saves locations
/// and goes through the unmodified <see cref="Abstractions.Loadouts.Synchronizers.DefaultSynchronizer"/>.
/// </summary>
public abstract class AStubbedGameIsolatedTest<TTest>(ITestOutputHelper helper) : AIsolatedGameTest<TTest, StubbedGame>(helper)
{
    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddGame<StubbedGame>()
            .AddUniversalGameLocator<StubbedGame>(Version.Parse("0.0.0"));
    }
}
