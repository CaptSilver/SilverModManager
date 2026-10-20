using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NexusMods.Platform.Proton.Launch;
using NexusMods.Platform.Proton.Protontricks;
using NexusMods.Platform.Proton.Steam;
using NexusMods.Platform.Proton.Verbs;
using NexusMods.Platform.Proton.ToolRunner;
using NexusMods.Platform.Proton.UserTools;
using NexusMods.Abstractions.Loadouts;
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
        services.AddSingleton<IToolProvider, UserToolProvider>();
        services.AddSingleton<IToolRunner, ToolRunner.ToolRunner>();
        // The doctor probes launch locks without needing the rest of the headless launch services.
        services.TryAddSingleton<IInstallationLockFactory, InstallationLockFactory>();
        services.TryAddSingleton<IProcessTracker>(_ => new ProcProcessTracker("/proc"));
        services.TryAddSingleton<LocalConfigVdfWriter>();
        return services.AddDoctorVerbs();
    }

    /// <summary>
    /// Keeps the launcher shim that Steam launch options point at current. Only the GUI profile registers
    /// this: it is the one process that knows which executable to bake in.
    /// </summary>
    public static IServiceCollection AddLauncherShim(this IServiceCollection services)
        => services.AddHostedService<LauncherShimRefresher>();
}
