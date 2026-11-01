using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Games.Generic;

namespace SilverModManager.Games.REEngine;

public static class Services
{
    /// <summary>Registers every row of <see cref="ReEngineGames.All"/> as a game with its launch tool.</summary>
    public static IServiceCollection AddReEngineGames(this IServiceCollection services)
    {
        services.AddSingleton<ReFrameworkMissingEmitter>();
        foreach (var definition in ReEngineGames.All)
            services.AddDefinedGame(definition, (provider, row) => new ReEngineGame(provider, row));
        return services;
    }
}
