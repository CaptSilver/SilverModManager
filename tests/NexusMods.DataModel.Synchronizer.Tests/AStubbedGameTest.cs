using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Games;
using NexusMods.Games.TestFramework;
using NexusMods.StandardGameLocators.TestHelpers;
using NexusMods.StandardGameLocators.TestHelpers.StubbedGames;
using Xunit;

namespace NexusMods.DataModel.Synchronizer.Tests;

/// <summary>
/// Base for tests that need the stubbed game: it has Game, Preferences and Saves locations
/// and goes through the unmodified <see cref="Abstractions.Loadouts.Synchronizers.DefaultSynchronizer"/>.
/// </summary>
public abstract class AStubbedGameTest<TTest>(ITestOutputHelper helper) : AIsolatedGameTest<TTest, StubbedGame>(helper)
{
    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddGame<StubbedGame>()
            .AddUniversalGameLocator<StubbedGame>(Version.Parse("0.0.0"));
    }
}
