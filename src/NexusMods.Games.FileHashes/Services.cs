using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NexusMods.Abstractions.Games.FileHashes;
using NexusMods.Abstractions.Games.FileHashes.Models;
using NexusMods.Sdk.Settings;

namespace NexusMods.Games.FileHashes;

public static class Services
{
    /// <summary>
    /// Add services related to the file hashes module.
    /// </summary>
    public static IServiceCollection AddFileHashes(this IServiceCollection services)
    {
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
