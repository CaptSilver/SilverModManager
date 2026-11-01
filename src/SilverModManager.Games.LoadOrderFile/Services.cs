using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Games.Generic;

namespace SilverModManager.Games.LoadOrderFile;

public static class Services
{
    /// <summary>Registers every row of <see cref="LoadOrderFileGames.All"/> as a game with its launch tool.</summary>
    public static IServiceCollection AddLoadOrderFileGames(this IServiceCollection services)
    {
        foreach (var definition in LoadOrderFileGames.All)
            services.AddDefinedGame(definition, (provider, row) => new LoadOrderFileGame(provider, row));
        return services;
    }
}
