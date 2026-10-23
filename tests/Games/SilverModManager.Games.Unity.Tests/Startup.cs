using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.GuidedInstallers;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Backend;
using NexusMods.Games.FOMOD;
using NexusMods.Games.TestFramework;
using NexusMods.Games.Unity.Tests.Fixtures;
using NexusMods.Sdk;
using NexusMods.StandardGameLocators.TestHelpers;

namespace NexusMods.Games.Unity.Tests;

public class Startup
{
    public void ConfigureServices(IServiceCollection container)
    {
        container
            .AddSingleton<IGuidedInstaller, NullGuidedInstaller>()
            .AddDefaultServicesForTesting()
            .AddUniversalGameLocator<TestUnityGame>(new Version("1.0"))
            .AddFomod()
            .AddLogging(builder => builder.AddXUnit())
            .AddGames()
            .AddGameServices()
            .AddLoadoutAbstractions()
            .Validate();
    }
}
