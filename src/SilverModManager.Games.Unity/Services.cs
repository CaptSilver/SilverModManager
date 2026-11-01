using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Games.Generic;
using SilverModManager.Games.Unity.BepInEx;

namespace SilverModManager.Games.Unity;

public static class Services
{
    /// <summary>Registers every row of <see cref="UnityGames.All"/> as a game with its launch tool.</summary>
    public static IServiceCollection AddUnityGames(this IServiceCollection services)
    {
        services.AddSingleton<BepInExMissingEmitter>();
        foreach (var definition in UnityGames.All)
            services.AddDefinedGame(definition, (provider, row) => new UnityGame(provider, row));
        return services;
    }
}
