using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.Larian.Engine;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.Games.Larian.DivinityOriginalSinEE;

public static class Services
{
    public static IServiceCollection AddDivinityOriginalSinEE(this IServiceCollection services)
    {
        services
            .AddLarianEngine()
            .AddGame<DivinityOriginalSinEE>()
            .AddSingleton<ITool, LarianRunGameTool<DivinityOriginalSinEE>>()
            .AddSettings<DivinityOriginalSinEESettings>();

        return services;
    }
}
