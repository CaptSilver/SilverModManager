using Microsoft.Extensions.DependencyInjection;
using NexusMods.Backend;
using NexusMods.CrossPlatform;
using NexusMods.Games.Generic;
using NexusMods.Games.TestFramework;
using NexusMods.Paths;
using NexusMods.Sdk.Settings;

namespace NexusMods.Abstractions.Games.Tests;

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
