using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Games.FileHashes;
using SilverModManager.Abstractions.Games.FileHashes.Models;
using SilverModManager.Abstractions.Games.Merge;
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
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IGameUpdateCheck, GameUpdateWaitingCheck>());
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
            .AddLocalFileHashModel()
            .AddAttestedFileModel()
            .AddEpicGameStoreBuildModel()
            .AddFileHashesQueriesSql()
            .AddSingleton<IFileHashesService, FileHashesService>()
            .AddSingleton<IVanillaTrust, BaselineVanillaTrust>()
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

    /// <summary>
    /// Refreshes a game when Steam finishes updating it while the app runs. Left out of
    /// <see cref="AddFileHashes"/> for the same reason as <see cref="AddLocalBaselineStartupPass"/>.
    /// </summary>
    public static IServiceCollection AddSteamUpdateWatcher(this IServiceCollection services)
    {
        return services
            .AddSingleton<StoreUpdateReaction>()
            .AddSingleton<IHostedService, SteamAppManifestWatcher>();
    }
}
