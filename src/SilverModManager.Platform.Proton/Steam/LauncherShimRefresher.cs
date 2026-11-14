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

            // With the app in Steam's library the shim is what Steam starts, so a dev build must not become it.
            if (IsDevBuildPath(target.ToString()))
            {
                logger.LogWarning("Not pointing the launcher shim at {Target}: it is a build output folder, and the shim is what Steam would start", target);
                return Task.CompletedTask;
            }

            var shim = new LauncherShim(LauncherShim.ResolveDataHome(environment, fileSystem));
            if (shim.Write(target)) logger.LogInformation("Wrote launcher shim {Shim} for {Target}", shim.ShimPath, target);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not refresh the launcher shim; Steam launch lines that use it may point at an old executable");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Whether the path has a <c>bin/Debug</c> or <c>bin/Release</c> segment.
    /// </summary>
    internal static bool IsDevBuildPath(string path)
        => path.Contains("/bin/Debug/", StringComparison.Ordinal) || path.Contains("/bin/Release/", StringComparison.Ordinal);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
