using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Games;
using SilverModManager.Platform.Proton.Launch;
using SilverModManager.Platform.Proton.Steam;
using SilverModManager.Platform.Proton.ToolRunner;
using SilverModManager.Platform.Proton.Verbs;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Platform.Proton;

public static class LaunchServices
{
    /// <summary>
    /// For a process that starts games but is not the launch: its own SIGTERM, SIGINT and SIGHUP keep their
    /// meaning while a game runs, instead of being swallowed or forwarded to the game. The window stops its games
    /// through the launch session.
    /// </summary>
    public static IServiceCollection LeaveSignalsToHost(this IServiceCollection services)
        => services.Replace(ServiceDescriptor.Singleton<IGameProcessFactory>(s => new GameProcessFactory(s.GetRequiredService<IRunningGameDetector>(), stop: null, leaveSignalsAlone: true, logger: s.GetService<ILogger<GameProcessFactory>>(), registry: s.GetService<IGameRegistry>())));

    /// <summary>
    /// The services every way of starting or waiting on a game shares: the clock, the registry of tools the app
    /// started, and the detector that answers whether a game is still running. Registered by the headless launch,
    /// by the prefix and tool services and by test graphs that build only part of the stack.
    /// </summary>
    public static IServiceCollection AddGameLaunchCore(this IServiceCollection services)
    {
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.TryAddSingleton<IStartedTools, StartedTools>();
        services.TryAddSingleton<IRunningGameDetector, RunningGameDetector>();
        return services;
    }

    /// <summary>
    /// Registers the services that let a game launch run without the GUI.
    /// </summary>
    public static IServiceCollection AddHeadlessLaunch(this IServiceCollection services)
    {
        services.TryAddSingleton<IEnvironmentReader, ProcessEnvironmentReader>();
        services.TryAddSingleton<IProcessTracker>(_ => new ProcProcessTracker("/proc"));
        services.AddGameLaunchCore();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ILaunchGate, RunningGameGate>());
        services.TryAddSingleton<IDesktopNotifier, DesktopNotifier>();
        services.TryAddSingleton<ISteamUriLauncher>(s => new SteamBinaryUriLauncher(
            s.GetRequiredService<IProcessRunner>(),
            s.GetRequiredService<IOSInterop>(),
            s.GetRequiredService<IEnvironmentReader>(),
            File.Exists));
        services.TryAddSingleton<ActiveLaunchSessions>();
        services.TryAddSingleton<IGameLaunchSession>(s => s.GetRequiredService<ActiveLaunchSessions>());
        services.TryAddSingleton<IGameProcessFactory, GameProcessFactory>();
        services.TryAddSingleton<ILaunchSynchronizer, SynchronizerServiceLaunchSynchronizer>();
        services.TryAddTransient<LaunchSession>();
        services.TryAddSingleton<LaunchFinish>();
        services.TryAddSingleton<ILaunchFinishMarkers, LaunchFinishMarkers>();
        services.TryAddSingleton<PendingLaunchFinisher>();
        services.TryAddSingleton<IPendingLaunchFinisher>(s => s.GetRequiredService<PendingLaunchFinisher>());
        services.TryAddSingleton<IInstallationLockFactory, InstallationLockFactory>();
        services.TryAddSingleton<LoadoutResolver>();
        services.TryAddSingleton<LocalConfigVdfWriter>();
        services.TryAddSingleton<ISteamLaunchOptionWriter, SteamLaunchOptionWriter>();
        return services.AddLaunchVerbs();
    }
}
