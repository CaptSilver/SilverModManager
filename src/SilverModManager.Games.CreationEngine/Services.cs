using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.Bethesda.Loot.Masterlists;
using SilverModManager.Games.CreationEngine.Loot;
using SilverModManager.Games.CreationEngine.Models;
using SilverModManager.Games.CreationEngine.SortOrder;
using NexusMods.Paths;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.Games.CreationEngine;

public static class Services
{
    public static IServiceCollection AddCreationEngine(this IServiceCollection services)
    {
        services.AddBethesdaGame<SkyrimSE.SkyrimSE>(KnownPaths.SKSE64Loader);
        services.AddBethesdaGame<SkyrimLE.SkyrimLE>(KnownPaths.SKSELoader);
        services.AddBethesdaGame<SkyrimVR.SkyrimVR>(KnownPaths.SKSEVRLoader);
        services.AddBethesdaGame<Enderal.Enderal>(KnownPaths.SKSELoader);
        services.AddBethesdaGame<EnderalSE.EnderalSE>(KnownPaths.SKSE64Loader);
        services.AddBethesdaGame<Fallout4.Fallout4>(KnownPaths.F4SELoader);
        services.AddBethesdaGame<Fallout4VR.Fallout4VR>(KnownPaths.F4SEVRLoader);
        services.AddBethesdaGame<Fallout76.Fallout76>(null);
        services.AddBethesdaGame<Starfield.Starfield>(KnownPaths.SFSELoader);
        services.AddBethesdaGame<Oblivion.Oblivion>(KnownPaths.OBSELoader);
        services.AddBethesdaGame<Fallout3.Fallout3>(KnownPaths.FOSELoader);
        services.AddBethesdaGame<FalloutNewVegas.FalloutNewVegas>(KnownPaths.NVSELoader);
        services.AddSingleton<Morrowind.OpenMw.IOpenMwConfigRoots, Morrowind.OpenMw.OpenMwConfigRoots>();
        services.AddBethesdaGame<Morrowind.Morrowind>(null);
        services.AddBethesdaGame<OblivionRemastered.OblivionRemastered>(KnownPaths.OBSE64Loader);

        services.AddSettings<LootMasterlistSettings>();
        services.AddSingleton(s => new LootDataDirectory(
            s.GetRequiredService<IFileSystem>().GetKnownPath(KnownPath.XDG_DATA_HOME) / ApplicationConstants.DataDirectoryName));
        services.AddSingleton<IMasterlistStore>(s => new CachingMasterlistStore(
            s.GetRequiredService<LootDataDirectory>().Path,
            new BundledMasterlistStore()));

        services.AddPluginSortOrderModel()
            .AddPluginSortOrderItemModel()
            .AddPluginListNoticeModel()
            .AddPluginQueriesSql();

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
        {
            services.AddSingleton(new ScriptExtenderLoader<TGame>(loader));
            services.AddSingleton<ITool>(s => RunGameViaScriptExtenderTool<TGame>.Create(s, loader));
        }
        else
            services.AddSingleton<ITool, RunGameTool<TGame>>();
        return services;
    }
}
