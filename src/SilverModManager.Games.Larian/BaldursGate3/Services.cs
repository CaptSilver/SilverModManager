using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.Larian.Engine;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.Games.Larian.BaldursGate3;

public static class Services
{
    public static IServiceCollection AddBaldursGate3(this IServiceCollection services)
    {
        services
            .AddLarianEngine()
            .AddGame<BaldursGate3>()
            .AddSingleton<ITool, LarianRunGameTool<BaldursGate3>>()
            .AddSettings<BaldursGate3Settings>();

        return services;
    }
}
