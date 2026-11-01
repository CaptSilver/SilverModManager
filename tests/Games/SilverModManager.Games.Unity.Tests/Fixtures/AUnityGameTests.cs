using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using NexusMods.Paths;
using SilverModManager.Games.TestFramework;
using SilverModManager.StandardGameLocators.TestHelpers;

namespace SilverModManager.Games.Unity.Tests.Fixtures;

/// <summary>
/// Runs a test against one of the real Unity games, registered the way the app registers them, with the library and
/// installer helpers a test needs to put an archive through the game's installers.
/// </summary>
public abstract class AUnityGameTests<TTest>(ITestOutputHelper outputHelper, UnityGameDefinition definition)
    : ALibraryArchiveInstallerTests<TTest>(outputHelper, definition.GameId)
{
    protected UnityGameDefinition Definition { get; } = definition;


    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return AddGameLocator(base.AddServices(services).AddUnityGames());
    }

    /// <summary>
    /// Registers the one locator that finds the game, in a folder that holds the game's executable under the name the
    /// definition gives it; a test that needs a Steam install with a prefix replaces it.
    /// </summary>
    protected virtual IServiceCollection AddGameLocator(IServiceCollection services)
        => services.AddUniversalGameLocator(
            definition.GameId,
            new Dictionary<RelativePath, byte[]> { [definition.Executable] = [] },
            locationsFileSystem: IsolatedKnownFolders.Create);
}
