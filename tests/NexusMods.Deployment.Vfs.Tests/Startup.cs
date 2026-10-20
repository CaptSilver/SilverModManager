using Microsoft.Extensions.DependencyInjection;
using NexusMods.Backend;
using NexusMods.Paths;
using NexusMods.Sdk.Settings;

namespace NexusMods.Deployment.Vfs.Tests;

public class Startup
{
    public void ConfigureServices(IServiceCollection services)
    {
        services
            .AddFileSystem()
            .AddSettingsManager()
            .AddLogging();
    }
}
