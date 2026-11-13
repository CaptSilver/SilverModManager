using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Games.Larian.BaldursGate3.Models;
using SilverModManager.Games.Larian.BaldursGate3.SortOrder;
using SilverModManager.Games.Larian.Engine.Utils;

namespace SilverModManager.Games.Larian.Engine;

public static class LarianEngineServices
{
    /// <summary>Marks that the shared services below are in the collection, so each Larian game can ask for them.</summary>
    private sealed class Registered;

    /// <summary>
    /// What every Larian game needs: the pak metadata pipeline, the game module catalog and the models the pak
    /// load order is stored in. Every Larian game's own <c>Add</c> method calls it, and calling it again is harmless.
    /// </summary>
    public static IServiceCollection AddLarianEngine(this IServiceCollection services)
    {
        if (services.Any(descriptor => descriptor.ServiceType == typeof(Registered))) return services;

        return services
            .AddSingleton<Registered>()
            .AddPipelines()
            .AddSingleton<GameModuleCatalog>()
            // models
            .AddBg3PakSortOrderModel()
            .AddBg3PakSortOrderItemModel()
            .AddBg3ModListNoticeModel()
            .AddBg3ModuleEntryModel()
            .AddBg3PakQueriesSql();
    }
}
