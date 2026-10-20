using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NexusMods.Sdk.Games;

namespace NexusMods.Platform.Proton;

public static class Services
{
    /// <summary>
    /// Registers the Proton services.
    /// </summary>
    public static IServiceCollection AddProton(this IServiceCollection services)
    {
        services.TryAddSingleton<IEnvironmentReader, ProcessEnvironmentReader>();
        return services.AddSingleton<IProtonResolver, ProtonResolver>();
    }
}
