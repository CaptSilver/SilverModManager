using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Secrets;
using SilverModManager.Networking.HttpDownloader;
using NexusMods.Paths;
using Xunit.DependencyInjection.Logging;

namespace SilverModManager.Networking.Steam.Tests;

public class Startup
{
    public void ConfigureServices(IServiceCollection services)
    {
        services
            .AddHttpDownloader()
            .AddSteam()
            .AddLoggingAuthenticationHandler()
            .AddSingleton<ISecretStore>(new InMemorySecretStore())
            .AddSecretStoreAuthStorage()
            .AddFileSystem()
            .AddLogging(builder => builder.AddXunitOutput().SetMinimumLevel(LogLevel.Trace));
    }
}

