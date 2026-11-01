using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Games.TestFramework;
using SilverModManager.StandardGameLocators.TestHelpers;

namespace SilverModManager.Games.Unity.Tests.Fixtures;

/// <summary>Runs a test against one of the real Unity games, registered the way the app registers them.</summary>
public abstract class AUnityGameTests<TTest>(ITestOutputHelper outputHelper, UnityGameDefinition definition)
    : AIsolatedDefinedGameTest<TTest>(outputHelper, definition.GameId)
{
    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return AddGameLocator(base.AddServices(services).AddUnityGames());
    }

    /// <summary>Registers the one locator that finds the game; a test that needs a Steam install with a prefix replaces it.</summary>
    protected virtual IServiceCollection AddGameLocator(IServiceCollection services)
        => services.AddUniversalGameLocator(definition.GameId, locationsFileSystem: IsolatedKnownFolders.Create);
}
