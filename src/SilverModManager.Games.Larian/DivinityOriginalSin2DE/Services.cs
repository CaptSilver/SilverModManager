using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.Larian.Engine;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.Games.Larian.DivinityOriginalSin2DE;

public static class Services
{
    public static IServiceCollection AddDivinityOriginalSin2DE(this IServiceCollection services)
    {
        services
            .AddLarianEngine()
            .AddGame<DivinityOriginalSin2DE>()
            .AddSingleton<ITool, LarianRunGameTool<DivinityOriginalSin2DE>>()
            .AddSettings<DivinityOriginalSin2DESettings>();

        return services;
    }
}
