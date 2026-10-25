using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Games.RedEngine.Cyberpunk2077.Emitters;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Sdk.Settings;
using SilverModManager.Games.RedEngine.Cyberpunk2077;
using SilverModManager.Games.RedEngine.Cyberpunk2077.Models;
using SilverModManager.Games.RedEngine.Cyberpunk2077.SortOrder;

namespace SilverModManager.Games.RedEngine;

public static class Services
{
    public static IServiceCollection AddRedEngineGames(this IServiceCollection services)
    {
        services.AddGame<Cyberpunk2077Game>()
            .AddRedModInfoFileModel()
            .AddRedModSortOrderModel()
            .AddRedModLoadoutGroupModel()
            .AddRedModSortOrderItemModel()
            .AddRedModQueriesSql()
            .AddSingleton<RedModSortOrderVariety, RedModSortOrderVariety>()
            .AddSingleton<ITool, RunCyberpunk2077Game>()
            .AddSingleton<ITool, RedModDeployTool>()
            // Diagnostics
            
            .AddSettings<Cyberpunk2077Settings>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IGameUpdateCheck, GameUpdatedSinceExtenderRanCheck>());
        return services;
    }
}
