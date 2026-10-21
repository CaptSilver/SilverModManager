using System.Reactive.Linq;
using DynamicData;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Games.FromSoft.EldenRing;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk.Jobs;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Games.FromSoft.Me3;

/// <summary>
/// Gives an Elden Ring loadout me3 once an install leaves it with mods and without it. It runs after the install job
/// completes and in the background, so the user's install never waits for the network; a failure is logged and
/// leaves the health check's critical, whose fix retries.
/// </summary>
public class Me3AutoInstaller(IJobMonitor monitor, IConnection connection, Me3HostProvisioner provisioner, ILogger<Me3AutoInstaller> logger) : IHostedService, IDisposable
{
    private IDisposable? _subscription;

    public virtual Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = monitor.GetObservableChangeSet<IInstallLoadoutItemJob>()
            .MergeMany(job => job.ObservableStatus.Where(status => status == JobStatus.Completed).Take(1).Select(_ => job))
            .Subscribe(job =>
            {
                if (job.Definition is IInstallLoadoutItemJob install) _ = Task.Run(() => OnInstallCompleted(install.LoadoutId));
            });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        _subscription = null;
        return Task.CompletedTask;
    }

    public void Dispose() => _subscription?.Dispose();

    /// <summary>Provisions me3 when the loadout is an Elden Ring one with mods. Never throws.</summary>
    public async Task OnInstallCompleted(LoadoutId loadoutId)
    {
        try
        {
            var loadout = Loadout.Load(connection.Db, loadoutId);
            if (!loadout.IsValid() || loadout.InstallationInstance.Game is not EldenRingGame) return;
            if (!ModdedLoadout.IsModded(loadout) || ModdedLoadout.HasHost(loadout)) return;

            await provisioner.EnsureInstalled(loadoutId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not install me3 into loadout {Loadout}; the health check offers to retry", loadoutId);
        }
    }
}
