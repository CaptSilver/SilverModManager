using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Games.FileHashes;
using SilverModManager.Abstractions.Games.FileHashes.Models;
using SilverModManager.Games.FileHashes.Emitters;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.Games.FileHashes;

public static class Services
{
    /// <summary>
    /// Add services related to the file hashes module.
    /// </summary>
    public static IServiceCollection AddFileHashes(this IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDiagnosticEmitter, NonVanillaGameFilesEmitter>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDiagnosticEmitter, GameMovedEmitter>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDiagnosticEmitter, GameUpdateEmitter>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IGameUpdateCheck, GameBuildChangedCheck>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IGameUpdateCheck, GameRecentlyUpdatedCheck>());
        services.TryAddSingleton(TimeProvider.System);

        return services.AddFileHashesVerbs()
            .AddPathHashRelationModel()
            .AddVersionDefinitionModel()
            .AddGogBuildModel()
            .AddGogDepotModel()
            .AddGogManifestModel()
            .AddSteamManifestModel()
            .AddLocalBaselineModel()
            .AddNonVanillaFileModel()
            .AddEpicGameStoreBuildModel()
            .AddFileHashesQueriesSql()
            .AddSingleton<IFileHashesService, FileHashesService>()
            .AddSingleton<IHostedService>(s => (IHostedService)s.GetRequiredService<IFileHashesService>())
            .AddSettings<FileHashesServiceSettings>()
            .AddHashRelationModel();
    }

    /// <summary>
    /// Updates the local baselines of managed games in the background after startup. Left out of
    /// <see cref="AddFileHashes"/> so that only the process showing the window runs it.
    /// </summary>
    public static IServiceCollection AddLocalBaselineStartupPass(this IServiceCollection services)
    {
        return services.AddSingleton<IHostedService, LocalBaselineStartupPass>();
    }
}
