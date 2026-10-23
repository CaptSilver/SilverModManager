using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SilverModManager.Platform.Proton.Doctor;
using SilverModManager.Platform.Proton.Launch;
using SilverModManager.Platform.Proton.Support;
using SilverModManager.Platform.Proton.Protontricks;
using SilverModManager.Platform.Proton.Steam;
using SilverModManager.Platform.Proton.Verbs;
using SilverModManager.Platform.Proton.ToolRunner;
using SilverModManager.Platform.Proton.UserTools;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Platform.Proton;

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
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDiagnosticEmitter, SteamLaunchOptionEmitter>());
        services.AddSingleton<IManagedInstallations, LoadoutManagedInstallations>();
        services.AddSingleton<ISupportBundleWriter, SupportBundleWriter>();
        return services.AddDoctorVerbs().AddDeploymentVerbs().AddSelftestVerbs();
    }

    /// <summary>
    /// Keeps the launcher shim that Steam launch options point at current. Only the GUI profile registers
    /// this: it is the one process that knows which executable to bake in.
    /// </summary>
    public static IServiceCollection AddLauncherShim(this IServiceCollection services)
        => services.AddHostedService<LauncherShimRefresher>();
}
