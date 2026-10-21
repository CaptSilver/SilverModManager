using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.GuidedInstallers;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Backend;
using NexusMods.Games.CreationEngine;
using NexusMods.Games.Declarative;
using NexusMods.Games.FromSoft;
using NexusMods.Games.Larian.BaldursGate3;
using NexusMods.Games.MountAndBlade2Bannerlord;
using NexusMods.Games.RedEngine;
using NexusMods.Games.StardewValley;
using NexusMods.Games.TestFramework;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.StandardGameLocators.TestHelpers;

namespace NexusMods.Games.Declarative.Tests;

public class Startup
{
    public void ConfigureServices(IServiceCollection container)
    {
        container
            .AddSingleton<IGuidedInstaller, NullGuidedInstaller>()
            .AddDefaultServicesForTesting()
            .AddUniversalGameLocator<CreationEngine.SkyrimSE.SkyrimSE>(new Version("1.6.1170"))
            .AddRedEngineGames()
            .AddStardewValley()
            .AddBaldursGate3()
            .AddCreationEngine()
            .AddMountAndBlade2Bannerlord()
            .AddFromSoftGames()
            .AddDeclarativeGames(new InMemoryFileSystem(OSInformation.FakeUnix))
            .AddLogging(builder => builder.AddXUnit())
            .AddGames()
            .AddGameServices()
            .AddLoadoutAbstractions()
            .Validate();
    }
}
