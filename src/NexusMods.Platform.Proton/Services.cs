using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NexusMods.Platform.Proton.Protontricks;
using NexusMods.Platform.Proton.ToolRunner;
using NexusMods.Sdk;
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
        services.AddSingleton<IToolRunStrategy, UmuStrategy>();
        services.AddSingleton<IToolRunStrategy, HeroicFlatpakStrategy>();
        services.AddSingleton<IToolRunStrategy, PlainWineStrategy>();

        services.AddSingleton<ProtontricksNativeDependency>();
        services.AddSingleton<ProtontricksFlatpakDependency>();
        services.AddSingleton<AggregateProtontricksDependency>();
        services.AddSingleton<IRuntimeDependency, AggregateProtontricksDependency>();
        services.AddSingleton<IToolRunStrategy, ProtontricksStrategy>();
        return services.AddSingleton<IToolRunner, ToolRunner.ToolRunner>();
    }
}
