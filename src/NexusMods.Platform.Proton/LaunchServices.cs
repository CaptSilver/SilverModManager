using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NexusMods.Platform.Proton.Launch;
using NexusMods.Sdk.Games;

namespace NexusMods.Platform.Proton;

public static class LaunchServices
{
    /// <summary>
    /// Registers the services that let a game launch run without the GUI.
    /// </summary>
    public static IServiceCollection AddHeadlessLaunch(this IServiceCollection services)
    {
        services.TryAddSingleton<IEnvironmentReader, ProcessEnvironmentReader>();
        services.TryAddSingleton<IProcessTracker>(_ => new ProcProcessTracker("/proc"));
        return services.AddSingleton<IInstallationLockFactory, InstallationLockFactory>();
    }
}
