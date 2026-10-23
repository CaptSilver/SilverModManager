using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Games.TestFramework;

namespace SilverModManager.Networking.ModUpdates.Tests;

public class Startup
{
    public void ConfigureServices(IServiceCollection container)
    {
        container.AddDefaultServicesForTesting();
    }
}

