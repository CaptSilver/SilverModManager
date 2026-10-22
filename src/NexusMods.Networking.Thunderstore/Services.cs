using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NexusMods.Sdk.Settings;

namespace NexusMods.Networking.Thunderstore;

/// <summary>
/// Service registration.
/// </summary>
[PublicAPI]
public static class Services
{
    /// <summary>
    /// Registers an implementation of <see cref="IThunderstoreApi"/> and the per-community <see cref="ThunderstoreIndexCache"/>.
    /// </summary>
    public static IServiceCollection AddThunderstore(this IServiceCollection serviceCollection)
    {
        serviceCollection.TryAddSingleton(TimeProvider.System);
        return serviceCollection
            .AddSettings<ThunderstoreSettings>()
            .AddSingleton<IThunderstoreApi>(p => new ThunderstoreApi(p.GetRequiredService<HttpClient>()))
            .AddSingleton<IPackageLookup>(p => new ApiPackageLookup(p.GetRequiredService<IThunderstoreApi>()))
            .AddSingleton<ThunderstoreDependencyResolver>()
            .AddSingleton<ThunderstoreUpdateChecker>()
            .AddSingleton(p => new ThunderstoreIndexCache(
                p.GetRequiredService<IThunderstoreApi>(),
                p.GetRequiredService<NexusMods.Paths.IFileSystem>(),
                p.GetRequiredService<ISettingsManager>(),
                p.GetRequiredService<TimeProvider>()));
    }
}
