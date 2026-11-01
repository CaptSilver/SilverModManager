using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Games;
using SilverModManager.Backend;
using SilverModManager.Games.TestFramework;
using SilverModManager.Sdk;

namespace SilverModManager.Games.Unreal.Tests;

public class Startup
{
    public void ConfigureServices(IServiceCollection container)
    {
        container
            .AddDefaultServicesForTesting()
            .AddLogging(builder => builder.AddXUnit())
            .AddUnrealGames()
            .AddGames()
            .AddGameServices()
            .Validate();
    }
}
