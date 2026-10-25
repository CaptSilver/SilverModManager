using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Games.TestFramework;
using NexusMods.Paths;
using SilverModManager.Sdk;

namespace SilverModManager.Networking.HttpDownloader.Tests;

public class Startup
{
    public void ConfigureServices(IServiceCollection container)
    {
        var prefix = FileSystem.Shared
            .FromUnsanitizedFullPath(TestScratch.Combine($"SilverModManager.Networking.HttpDownloader.Tests-{Guid.NewGuid()}"));

        container
            .AddDefaultServicesForTesting(prefix)
            .AddSingleton<LocalHttpServer>()
            .AddLogging(builder => builder.AddXUnit())
            .Validate();
    }
}

