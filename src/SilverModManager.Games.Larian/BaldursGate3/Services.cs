using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Sdk.Settings;
using SilverModManager.Games.Larian.BaldursGate3.Emitters;
using SilverModManager.Games.Larian.BaldursGate3.Models;
using SilverModManager.Games.Larian.BaldursGate3.SortOrder;
using SilverModManager.Games.Larian.BaldursGate3.RunGameTools;
using SilverModManager.Games.Larian.BaldursGate3.Utils;

namespace SilverModManager.Games.Larian.BaldursGate3;

public static class Services
{
    public static IServiceCollection AddBaldursGate3(this IServiceCollection services)
    {
        services
            .AddGame<BaldursGate3>()
            .AddSingleton<ITool, BG3RunGameTool>()
            .AddSettings<BaldursGate3Settings>()
            .AddPipelines()
            .AddSingleton<GameModuleCatalog>()
            // diagnostics
            .AddSingleton<DependencyDiagnosticEmitter>()
            // models
            .AddBg3PakSortOrderModel()
            .AddBg3PakSortOrderItemModel()
            .AddBg3ModListNoticeModel()
            .AddBg3ModuleEntryModel()
            .AddBg3PakQueriesSql();

        return services;
    }
}
