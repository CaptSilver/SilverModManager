using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.GuidedInstallers;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Backend;
using SilverModManager.Games.Larian.BaldursGate3;
using SilverModManager.Games.Larian.DivinityOriginalSin2DE;
using SilverModManager.Games.TestFramework;
using SilverModManager.Sdk;
using SilverModManager.StandardGameLocators.TestHelpers;

namespace SilverModManager.Games.Larian.Tests;

public class Startup
{
    public void ConfigureServices(IServiceCollection container)
    {
        container
            .AddSingleton<IGuidedInstaller, NullGuidedInstaller>()
            .AddDefaultServicesForTesting()
            .AddUniversalGameLocator<Larian.BaldursGate3.BaldursGate3>(new Version("1.61"), locationsFileSystem: IsolatedKnownFolders.Create)
            .AddUniversalGameLocator<Larian.DivinityOriginalSin2DE.DivinityOriginalSin2DE>(new Version("3.6.117"), locationsFileSystem: IsolatedKnownFolders.Create)
            .AddBaldursGate3()
            .AddDivinityOriginalSin2DE()
            .AddLogging(builder => builder.AddXUnit())
            .AddGames()
            .AddGameServices()
            .AddLoadoutAbstractions()
            .Validate();
    }
}
