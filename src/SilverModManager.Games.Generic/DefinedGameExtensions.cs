using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.Generic;

public static class DefinedGameExtensions
{
    /// <summary>
    /// Registers the game <paramref name="factory"/> builds from <paramref name="definition"/>, and the tool that
    /// launches it. Several definitions share one game type, so the instance is registered under the game's id and
    /// <see cref="IGame"/>, <see cref="IGameData"/> and the tool all resolve that one instance.
    /// </summary>
    public static IServiceCollection AddDefinedGame<TDefinition, TGame>(
        this IServiceCollection services,
        TDefinition definition,
        Func<IServiceProvider, TDefinition, TGame> factory)
        where TDefinition : IGameDefinition
        where TGame : class, IGame
    {
        var key = definition.GameId.ToString();
        services.AddKeyedSingleton<IGame>(key, (provider, _) => factory(provider, definition));
        services.AddSingleton<IGame>(provider => provider.GetRequiredKeyedService<IGame>(key));
        services.AddSingleton<IGameData>(provider => provider.GetRequiredKeyedService<IGame>(key));
        services.AddSingleton<ITool>(provider => new RunGameTool(provider, provider.GetRequiredKeyedService<IGame>(key)));
        return services;
    }
}
