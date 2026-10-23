using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.Unity.BepInEx;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.Unity;

public static class Services
{
    /// <summary>Registers every Unity game and its launch tool.</summary>
    public static IServiceCollection AddUnityGames(this IServiceCollection services)
    {
        return services
            .AddSingleton<BepInExMissingEmitter>()
            .AddGame<Repo.Repo>()
            .AddSingleton<ITool, RunGameTool<Repo.Repo>>()
            .AddGame<Peak.Peak>()
            .AddSingleton<ITool, RunGameTool<Peak.Peak>>();
    }
}
