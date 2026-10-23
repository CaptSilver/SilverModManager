using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using SilverModManager.Sdk;

namespace SilverModManager.Platform.Proton.Steam;

/// <summary>
/// Points the launcher shim at the executable this app instance was started as. A failure is logged and does
/// not stop the app: the shim only matters once the user has put a launch line into Steam.
/// </summary>
internal sealed class LauncherShimRefresher(IOSInterop interop, IEnvironmentReader environment, IFileSystem fileSystem, ILogger<LauncherShimRefresher> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Resolves $APPIMAGE first, so an AppImage's shim survives the mount point changing per run.
            var target = interop.GetRunningExecutablePath(out _);
            var shim = new LauncherShim(LauncherShim.ResolveDataHome(environment, fileSystem));
            if (shim.Write(target)) logger.LogInformation("Wrote launcher shim {Shim} for {Target}", shim.ShimPath, target);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not refresh the launcher shim; Steam launch lines that use it may point at an old executable");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
