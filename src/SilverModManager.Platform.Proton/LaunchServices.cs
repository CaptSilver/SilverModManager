using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SilverModManager.Abstractions.Games;
using SilverModManager.Platform.Proton.Launch;
using SilverModManager.Platform.Proton.Steam;
using SilverModManager.Platform.Proton.Verbs;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Platform.Proton;

public static class LaunchServices
{
    /// <summary>
    /// Registers the services that let a game launch run without the GUI.
    /// </summary>
    public static IServiceCollection AddHeadlessLaunch(this IServiceCollection services)
    {
        services.TryAddSingleton<IEnvironmentReader, ProcessEnvironmentReader>();
        services.TryAddSingleton<IProcessTracker>(_ => new ProcProcessTracker("/proc"));
        services.TryAddSingleton<IDesktopNotifier, DesktopNotifier>();
        services.TryAddSingleton<ActiveLaunchSessions>();
        services.TryAddSingleton<IGameLaunchSession>(s => s.GetRequiredService<ActiveLaunchSessions>());
        services.TryAddSingleton<IGameProcessFactory, GameProcessFactory>();
        services.TryAddSingleton<ILaunchSynchronizer, SynchronizerServiceLaunchSynchronizer>();
        services.TryAddTransient<LaunchSession>();
        services.TryAddSingleton<IInstallationLockFactory, InstallationLockFactory>();
        services.TryAddSingleton<LoadoutResolver>();
        services.TryAddSingleton<LocalConfigVdfWriter>();
        services.TryAddSingleton<ISteamLaunchOptionWriter, SteamLaunchOptionWriter>();
        return services.AddLaunchVerbs();
    }
}
