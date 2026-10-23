using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Sdk.Settings;
using SilverModManager.Backend;
using NexusMods.Paths;
using Xunit.DependencyInjection;

namespace SilverModManager.CrossPlatform.Tests;

public class Startup
{
    public void ConfigureServices(IServiceCollection container)
    {
        container
            .AddSingleton<TimeProvider>(_ => TimeProvider.System)
            .AddSettingsManager()
            .AddSettings<LoggingSettings>()
            .AddFileSystem()
            .AddOSInterop()
            .AddRuntimeDependencies()
            .RemoveRuntimeDependencyChecker()
            .AddLogging(builder => builder.AddXUnit());
    }
}

