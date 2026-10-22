using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Sdk.Games;

namespace NexusMods.Games.Unity;

public static class Services
{
    /// <summary>Registers every Unity game and its launch tool.</summary>
    public static IServiceCollection AddUnityGames(this IServiceCollection services)
    {
        return services
            .AddGame<Repo.Repo>()
            .AddSingleton<ITool, RunGameTool<Repo.Repo>>()
            .AddGame<Peak.Peak>()
            .AddSingleton<ITool, RunGameTool<Peak.Peak>>();
    }
}
