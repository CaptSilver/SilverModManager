using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NexusMods.Abstractions.Loadouts.Deployment;
using NexusMods.Sdk.Settings;

namespace NexusMods.Deployment.Staging;

public static class Services
{
    /// <summary>
    /// Registers <see cref="ILooseStagingStore"/> and its settings.
    /// </summary>
    public static IServiceCollection AddLooseStagingStore(this IServiceCollection services)
    {
        services.AddSettings<LooseStagingStoreSettings>();
        services.TryAddSingleton<IFilesystemLocator, StatFilesystemLocator>();
        services.TryAddSingleton<ILooseStagingStore, LooseStagingStore>();
        return services;
    }
}
