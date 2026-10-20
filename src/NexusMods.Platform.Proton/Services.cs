using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NexusMods.Platform.Proton.ToolRunner;
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
        services.AddSingleton<IPrefixResolver, PrefixResolver>();
        services.AddSingleton<IProtonResolver, ProtonResolver>();
        services.AddSingleton<IToolRunStrategy, SteamProtonStrategy>();
        return services.AddSingleton<IToolRunner, ToolRunner.ToolRunner>();
    }
}
