using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Backend;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.TestFramework;

namespace SilverModManager.Platform.Proton.Tests;

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
