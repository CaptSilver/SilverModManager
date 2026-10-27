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

    /// <summary>
    /// Adds <see cref="MergeEngine"/>, which serves every game that implements <see cref="IHasMergeGame"/>. It needs an
    /// <see cref="IVanillaTrust"/>, so it is not part of <see cref="AddMergeEngine"/>: a host that has none must not
    /// register it, or <see cref="MergeEngines"/> cannot be resolved.
    /// </summary>
    public static IServiceCollection AddTextMergeEngine(this IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMergeEngine, MergeEngine>());
        services.TryAddSingleton<EngineMergeDiagnosticsEmitter>();
        return services;
    }
}
