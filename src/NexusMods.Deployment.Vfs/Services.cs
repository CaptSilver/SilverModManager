using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NexusMods.Sdk.Settings;

namespace NexusMods.Deployment.Vfs;

public static class Services
{
    /// <summary>
    /// Registers the catalog model and what promotion needs. No deployment backend is added and no
    /// root is routed here: nothing mounts the tree yet. The model is added here, and this is called
    /// from the data model, so every host that builds the data model knows the catalog's attributes.
    /// </summary>
    public static IServiceCollection AddVfsCore(this IServiceCollection services)
    {
        services.AddVfsCatalogEntryModel();
        services.AddSettings<VfsSettings>();
        services.TryAddSingleton<IVfsCatalog, VfsCatalog>();
        services.TryAddSingleton<IFileHasher, CatalogBackedHasher>();
        services.TryAddSingleton<IBackingScanner, BackingScanner>();
        services.TryAddSingleton<IPromotionSink, LoadoutPromotionSink>();
        services.TryAddSingleton<StagingPromoterFactory>();
        return services;
    }
}
