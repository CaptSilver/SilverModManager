using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Backend;
using NexusMods.Paths;

namespace SilverModManager.Deployment.Fuse.Tests;

public class Startup
{
    public void ConfigureServices(IServiceCollection services)
    {
        services
            .AddFileSystem()
            .AddLogging();
    }
}
