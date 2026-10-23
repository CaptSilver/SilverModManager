using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;

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
}
