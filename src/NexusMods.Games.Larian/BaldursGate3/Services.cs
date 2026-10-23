using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Sdk.Settings;
using NexusMods.Games.Larian.BaldursGate3.Emitters;
using NexusMods.Games.Larian.BaldursGate3.Models;
using NexusMods.Games.Larian.BaldursGate3.SortOrder;
using NexusMods.Games.Larian.BaldursGate3.RunGameTools;
using NexusMods.Games.Larian.BaldursGate3.Utils;

namespace NexusMods.Games.Larian.BaldursGate3;

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
