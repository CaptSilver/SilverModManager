using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace SilverModManager.Abstractions.Downloads;

/// <summary>
/// Extension methods.
/// </summary>
[PublicAPI]
public static class Services
{
    /// <summary>
    /// Extension method.
    /// </summary>
    public static IServiceCollection AddDownloadModels(this IServiceCollection serviceCollection)
    {
        return serviceCollection;
    }

    /// <summary>
    /// The throttle and the lookup that every <see cref="IUrlDownloader"/> shares. Safe to call from each project that
    /// registers a downloader: the first registration stays.
    /// </summary>
    public static IServiceCollection AddUrlDownloading(this IServiceCollection serviceCollection)
    {
        serviceCollection.TryAddSingleton<HostThrottle>();
        serviceCollection.TryAddSingleton<UrlDownloaders>();
        return serviceCollection;
    }
}
