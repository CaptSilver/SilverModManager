using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Backend;
using SilverModManager.CrossPlatform;
using SilverModManager.Games.Generic;
using SilverModManager.Games.TestFramework;
using NexusMods.Paths;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.Abstractions.Games.Tests;

public class Startup
{
    public void ConfigureServices(IServiceCollection container)
    {
        container
            .AddSingleton<TimeProvider>(_ => TimeProvider.System)
            .AddSettings<LoggingSettings>()
            .AddSettingsManager()
            .AddFileSystem()
            .AddOSInterop()
            .AddRuntimeDependencies()
            .RemoveRuntimeDependencyChecker()
            .AddGenericGameSupport()
            .AddFakeProcessTracker();
    }
}
