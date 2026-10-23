using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NexusMods.Games.FromSoft.Diagnostics;
using NexusMods.Platform.Proton.Launch;

namespace NexusMods.Games.FromSoft.Build;

public static class Services
{
    /// <summary>
    /// Registers the build stamp of FromSoft games. The gate is added here, after the gates registered before this
    /// call, so it runs last.
    /// </summary>
    public static IServiceCollection AddFromSoftBuild(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        return services.AddFromSoftBuildStampModel()
            .AddSingleton<BuildStampStore>()
            .AddSingleton<ILaunchGate, BuildStampGate>()
            .AddSingleton<IFromSoftDiagnosticEmitter, BuildDriftEmitter>();
    }
}
