using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Backend;
using SilverModManager.Games.TestFramework;
using SilverModManager.StandardGameLocators.TestHelpers;
using SilverModManager.StandardGameLocators.TestHelpers.StubbedGames;

namespace SilverModManager.Abstractions.Games.Merge.Tests;

public class Startup
{
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddDefaultServicesForTesting()
            .AddGameServices()
            .AddLoadoutAbstractions()
            .AddGames()
            .AddRecordingDeploymentBackend()
            .AddGame<StubbedGame>()
            .AddUniversalGameLocator<StubbedGame>(Version.Parse("0.0.0"));
    }
}
