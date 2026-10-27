using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace SilverModManager.Abstractions.Games.Merge;

public static class Services
{
    /// <summary>
    /// Registers the engine registry and the shared <see cref="IMergeReports"/>, and no engine. Whoever owns an engine
    /// adds it with <c>TryAddEnumerable(ServiceDescriptor.Singleton&lt;IMergeEngine, T&gt;())</c>:
    /// <c>TryAddSingleton</c> keys on the service type and would skip a second engine.
    /// </summary>
    public static IServiceCollection AddMergeEngine(this IServiceCollection services)
    {
        services.TryAddSingleton<MergeEngines>();
        services.TryAddSingleton<IMergeReports, EngineMergeReports>();
        return services;
    }
}
