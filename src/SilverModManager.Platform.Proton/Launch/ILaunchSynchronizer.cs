using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.DataModel.Synchronizer;

namespace SilverModManager.Platform.Proton.Launch;

/// <summary>
/// The synchronize entry a launch uses on itself. The public one refuses while a launch is running, which
/// a launch's own applying and ingesting must not be.
/// </summary>
public interface ILaunchSynchronizer
{
    /// <summary>
    /// Applies the loadout to the game folder, or ingests the folder's changes into it.
    /// </summary>
    Task Synchronize(LoadoutId loadoutId);
}

/// <summary>
/// Goes through <see cref="SynchronizerService"/> so the semaphore and the loadout and game state locks still
/// apply, and skips only the running-game check.
/// </summary>
internal sealed class SynchronizerServiceLaunchSynchronizer(IServiceProvider serviceProvider) : ILaunchSynchronizer
{
    public Task Synchronize(LoadoutId loadoutId)
    {
        var service = serviceProvider.GetRequiredService<ISynchronizerService>();
        if (service is not SynchronizerService concrete)
            throw new InvalidOperationException($"{service.GetType()} has no launch-session entry; a launch cannot apply its loadout through it");
        return concrete.SynchronizeFromLaunchSession(loadoutId);
    }
}
