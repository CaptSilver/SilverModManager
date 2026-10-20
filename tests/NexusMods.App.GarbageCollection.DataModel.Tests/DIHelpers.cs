using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Backend;
using NexusMods.Deployment.Staging;
using NexusMods.Games.TestFramework;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.Settings;
using NexusMods.StandardGameLocators.TestHelpers;
using NexusMods.StandardGameLocators.TestHelpers.StubbedGames;

namespace NexusMods.App.GarbageCollection.DataModel.Tests;

public static class DIHelpers
{
    public static void ConfigureServices(IServiceCollection services)
    {
        services.AddDefaultServicesForTesting()
            .AddGameServices()
            .AddLoadoutAbstractions()
            .AddGames()
            .OverrideSettingsForTests<LooseStagingStoreSettings>(settings => settings with
            {
                Location = new ConfigurablePath(KnownPath.EntryDirectory, $"LooseStaging.{Guid.NewGuid()}"),
            })
            .AddRecordingDeploymentBackend()
            .AddGame<StubbedGame>()
            .AddUniversalGameLocator<StubbedGame>(Version.Parse("0.0.0"));
    }
}
