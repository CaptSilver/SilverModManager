using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Sdk.Games;

namespace NexusMods.Games.CreationEngine;

public static class Services
{
    public static IServiceCollection AddCreationEngine(this IServiceCollection services)
    {
        services.AddBethesdaGame<SkyrimSE.SkyrimSE>(KnownPaths.SKSE64Loader);
        services.AddBethesdaGame<Fallout4.Fallout4>(KnownPaths.F4SELoader);

        return services;
    }

    /// <summary>
    /// Registers a Bethesda title and the tool that launches it, through its script extender when it has one.
    /// </summary>
    public static IServiceCollection AddBethesdaGame<TGame>(this IServiceCollection services, GamePath? scriptExtenderLoader)
        where TGame : class, IGame
    {
        services.AddGame<TGame>();
        if (scriptExtenderLoader is { } loader)
            services.AddSingleton<ITool>(s => RunGameViaScriptExtenderTool<TGame>.Create(s, loader));
        else
            services.AddSingleton<ITool, RunGameTool<TGame>>();
        return services;
    }
}
