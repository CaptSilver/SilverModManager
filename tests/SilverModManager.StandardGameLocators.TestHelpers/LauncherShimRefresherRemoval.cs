using Microsoft.Extensions.DependencyInjection;

namespace NexusMods.StandardGameLocators.TestHelpers;

public static class LauncherShimRefresherRemoval
{
    // Internal to Platform.Proton, so it is matched by name.
    private const string RefresherTypeName = "NexusMods.Platform.Proton.Steam.LauncherShimRefresher";

    /// <summary>
    /// Removes the GUI graph's launcher shim refresher, which on host start would point the user's Steam launcher
    /// shim at the test runner.
    /// </summary>
    public static IServiceCollection RemoveLauncherShimRefresher(this IServiceCollection services)
    {
        var refreshers = services.Where(d => d.ImplementationType?.FullName == RefresherTypeName).ToArray();
        if (refreshers.Length == 0)
            throw new InvalidOperationException($"{RefresherTypeName} is not registered; if it was renamed, update this helper");

        foreach (var descriptor in refreshers) services.Remove(descriptor);
        return services;
    }
}
