using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Games.TestFramework;
using SilverModManager.StandardGameLocators.TestHelpers;

namespace SilverModManager.Games.Unreal.Tests.Fixtures;

/// <summary>
/// Runs a test against one of the real Unreal games, registered the way the app registers them, with the library and
/// installer helpers a test needs to put an archive through the game's installer.
/// </summary>
public abstract class AUnrealGameTests<TTest>(ITestOutputHelper outputHelper, UnrealGameDefinition definition)
    : ALibraryArchiveInstallerTests<TTest>(outputHelper, definition.GameId)
{
    protected UnrealGameDefinition Definition { get; } = definition;

    protected override IServiceCollection AddServices(IServiceCollection services)
        => AddGameLocator(base.AddServices(services).AddUnrealGames());

    /// <summary>
    /// Registers the one locator that finds the game, in a folder that holds the game's executable under the name the
    /// definition gives it.
    /// </summary>
    protected virtual IServiceCollection AddGameLocator(IServiceCollection services)
        => services.AddUniversalGameLocator(
            Definition.GameId,
            new Dictionary<RelativePath, byte[]> { [Definition.Executable] = [] },
            locationsFileSystem: IsolatedKnownFolders.Create);
}
