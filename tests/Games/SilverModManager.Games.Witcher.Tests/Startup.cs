using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.GuidedInstallers;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Backend;
using SilverModManager.Games.TestFramework;
using SilverModManager.Games.Generic.Simple;
using SilverModManager.Games.Witcher.Witcher3;
using SilverModManager.Sdk;
using SilverModManager.StandardGameLocators.TestHelpers;

namespace SilverModManager.Games.Witcher.Tests;

public class Startup
{
    public void ConfigureServices(IServiceCollection container)
    {
        container
            .AddSingleton<IGuidedInstaller, NullGuidedInstaller>()
            .AddDefaultServicesForTesting()
            .AddUniversalGameLocator(SimpleGames.Witcher1.GameId, locationsFileSystem: IsolatedKnownFolders.Create)
            .AddUniversalGameLocator(SimpleGames.Witcher2.GameId, locationsFileSystem: IsolatedKnownFolders.Create)
            .AddUniversalGameLocator<Witcher3Game>(new Version("4.0"), locationsFileSystem: IsolatedKnownFolders.Create)
            .AddSimpleGames()
            .AddWitcherGames()
            .AddLogging(builder => builder.AddXUnit())
            .AddGames()
            .AddGameServices()
            .AddLoadoutAbstractions()
            .Validate();
    }
}
