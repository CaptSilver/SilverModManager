using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Sdk.Settings;
using SilverModManager.Games.MountAndBlade2Bannerlord.Installers;
using SilverModManager.Games.MountAndBlade2Bannerlord.LauncherManager;
using SilverModManager.Games.MountAndBlade2Bannerlord.Models;

namespace SilverModManager.Games.MountAndBlade2Bannerlord;

public static class Services
{
    public static IServiceCollection AddMountAndBlade2Bannerlord(this IServiceCollection services)
    {
        return services
            .AddGame<Bannerlord>()
            .AddSingleton<ITool, BannerlordRunGameTool>()

            // Installers
            .AddSingleton<BLSEInstaller>()
            .AddSingleton<BannerlordModInstaller>()

            // Diagnostics

            // Attributes
            .AddBannerlordModuleLoadoutItemModel()
            .AddModuleInfoFileLoadoutFileModel()
   
            // Misc
            .AddSettings<BannerlordSettings>()
            .AddSingleton<LauncherManagerFactory>()
            .AddSingleton<FileSystemProvider>()
            .AddSingleton<NotificationProvider>()
            .AddSingleton<DialogProvider>()
            
            // Pipelines
            .AddPipelines();
    }
}
