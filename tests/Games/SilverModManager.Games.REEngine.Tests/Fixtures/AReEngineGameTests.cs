using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Games.TestFramework;
using SilverModManager.StandardGameLocators.TestHelpers;

namespace SilverModManager.Games.REEngine.Tests.Fixtures;

/// <summary>
/// Runs a test against one of the real RE Engine games, registered the way the app registers them, with the library and
/// installer helpers a test needs to put an archive through the game's installers.
/// </summary>
public abstract class AReEngineGameTests<TTest>(ITestOutputHelper outputHelper, ReEngineGameDefinition definition)
    : ALibraryArchiveInstallerTests<TTest>(outputHelper, definition.GameId)
{
    protected ReEngineGameDefinition Definition { get; } = definition;

    protected override IServiceCollection AddServices(IServiceCollection services)
        => AddGameLocator(base.AddServices(services).AddReEngineGames());

    /// <summary>
    /// Registers the one locator that finds the game, in a folder that holds the game's executable under the name the
    /// definition gives it; a test that needs a Steam install with a prefix replaces it.
    /// </summary>
    protected virtual IServiceCollection AddGameLocator(IServiceCollection services)
        => services.AddUniversalGameLocator(
            Definition.GameId,
            new Dictionary<RelativePath, byte[]> { [Definition.Executable] = [] },
            locationsFileSystem: IsolatedKnownFolders.Create);
}
