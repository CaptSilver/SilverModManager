using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Games.Generic;

namespace SilverModManager.Games.Unreal;

public static class Services
{
    /// <summary>Registers every row of <see cref="UnrealGames.All"/> as a game with its launch tool.</summary>
    public static IServiceCollection AddUnrealGames(this IServiceCollection services)
    {
        foreach (var definition in UnrealGames.All)
            services.AddDefinedGame(definition, (provider, row) => new UnrealGame(provider, row));
        return services;
    }
}
