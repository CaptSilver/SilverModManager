using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Backend;
using NexusMods.Games.TestFramework;

namespace NexusMods.Deployment.Vfs.Tests;

public class Startup
{
    public void ConfigureServices(IServiceCollection services)
    {
        services
            .AddGameServices()
            .AddLoadoutAbstractions()
            .AddDefaultServicesForTesting();
    }
}
