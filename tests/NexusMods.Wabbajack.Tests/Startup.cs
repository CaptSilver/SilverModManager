using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Backend;
using NexusMods.Games.TestFramework;
using NexusMods.Sdk;

namespace NexusMods.Wabbajack.Tests;

public class Startup
{
    public void ConfigureServices(IServiceCollection services)
    {
        services
            .AddDefaultServicesForTesting()
            .AddGames()
            .AddGameServices()
            .AddLoadoutAbstractions();
    }
}
