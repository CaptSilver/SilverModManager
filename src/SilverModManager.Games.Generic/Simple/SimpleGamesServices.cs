using Microsoft.Extensions.DependencyInjection;

namespace SilverModManager.Games.Generic.Simple;

public static class SimpleGamesServices
{
    /// <summary>Registers every row of <see cref="SimpleGames.All"/> as a game with its launch tool.</summary>
    public static IServiceCollection AddSimpleGames(this IServiceCollection services)
    {
        foreach (var definition in SimpleGames.All)
            services.AddDefinedGame(definition, (provider, row) => new SimpleGame(provider, row));
        return services;
    }
}
