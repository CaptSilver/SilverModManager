using Microsoft.Extensions.DependencyInjection;
using NexusMods.Backend;
using NexusMods.Paths;

namespace NexusMods.Deployment.Fuse.Tests;

public class Startup
{
    public void ConfigureServices(IServiceCollection services)
    {
        services
            .AddFileSystem()
            .AddLogging();
    }
}
