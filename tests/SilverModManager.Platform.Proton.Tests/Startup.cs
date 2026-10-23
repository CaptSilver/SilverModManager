using Microsoft.Extensions.DependencyInjection;
using NexusMods.Backend;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Games.TestFramework;

namespace NexusMods.Platform.Proton.Tests;

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
