using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.Larian.Engine;
using SilverModManager.Games.Larian.Engine.Utils;
using SilverModManager.Sdk.Settings;
using SilverModManager.Games.Larian.BaldursGate3.Models;
using SilverModManager.Games.Larian.BaldursGate3.SortOrder;

namespace SilverModManager.Games.Larian.BaldursGate3;

public static class Services
{
    public static IServiceCollection AddBaldursGate3(this IServiceCollection services)
    {
        services
            .AddGame<BaldursGate3>()
            .AddSingleton<ITool, LarianRunGameTool<BaldursGate3>>()
            .AddSettings<BaldursGate3Settings>()
            .AddPipelines()
            .AddSingleton<GameModuleCatalog>()
            // models
            .AddBg3PakSortOrderModel()
            .AddBg3PakSortOrderItemModel()
            .AddBg3ModListNoticeModel()
            .AddBg3ModuleEntryModel()
            .AddBg3PakQueriesSql();

        return services;
    }
}
