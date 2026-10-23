using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.StardewValley.Emitters;
using SilverModManager.Games.StardewValley.Installers;
using SilverModManager.Games.StardewValley.Models;
using SilverModManager.Games.StardewValley.RunGameTools;
using SilverModManager.Games.StardewValley.WebAPI;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.Games.StardewValley;

public static class Services
{
    public static IServiceCollection AddStardewValley(this IServiceCollection services)
    {
        return services
            .AddGame<StardewValley>()
            .AddSingleton<ITool, SmapiRunGameTool>()

            // Installers
            .AddSingleton<SMAPIInstaller>()
            .AddSingleton<GenericInstaller>()

            // Diagnostics
            .AddSingleton<DependencyDiagnosticEmitter>()
            .AddSingleton<MissingSMAPIEmitter>()
            .AddSingleton<SMAPIModDatabaseCompatibilityDiagnosticEmitter>()
            .AddSingleton<SMAPIGameVersionDiagnosticEmitter>()
            .AddSingleton<VersionDiagnosticEmitter>()
            .AddSingleton<ModOverwritesGameFilesEmitter>()

            // Attributes
            .AddSMAPILoadoutItemModel()
            .AddSMAPIModDatabaseLoadoutFileModel()
            .AddSMAPIModLoadoutItemModel()
            .AddSMAPIManifestLoadoutFileModel()

            // Misc
            .AddSingleton<ISMAPIWebApi, SMAPIWebApi>()
            .AddSettings<StardewValleySettings>()

            // Pipelines
            .AddPipelines();
    }
}
