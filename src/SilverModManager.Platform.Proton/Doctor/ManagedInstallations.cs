using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Deployment;
using NexusMods.Abstractions.Loadouts.Synchronizers.Deployment;
using NexusMods.Abstractions.Games;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Platform.Proton.Doctor;

/// <summary>
/// The game installations that have a visible loadout.
/// </summary>
public interface IManagedInstallations
{
    IReadOnlyList<ManagedInstallation> List();
}

/// <summary>
/// Finds the managed installations by walking the loadouts in the database, so the doctor verb and the
/// support bundle describe the same set of games.
/// </summary>
internal sealed class LoadoutManagedInstallations(
    IConnection connection,
    IGameRegistry registry,
    IDeploymentPlanStore plans,
    IEnumerable<IDeploymentBackend> backends,
    IDeploymentBackendSelector selector,
    IGameLaunchSession launchSession) : IManagedInstallations
{
    public IReadOnlyList<ManagedInstallation> List()
    {
        var db = connection.Db;
        var registered = backends.Select(b => b.Id).ToHashSet();
        return Loadout.All(db)
            .Where(loadout => loadout.IsVisible())
            .Select(loadout => (Loadout: loadout, Found: registry.TryGetGameInstallation(loadout, out var installation) ? installation : null))
            .Where(x => x.Found is not null)
            .GroupBy(x => x.Found!)
            .Select(group => new ManagedInstallation(
                group.Key,
                group.Select(x => x.Loadout.Name).Order(StringComparer.Ordinal).ToArray(),
                DoctorDeploymentRoot.From(plans.Load(db, group.First().Loadout.InstallationId), group.Key.Locations, registered, AvailableFor(group.Key))))
            .ToArray();
    }

    // The chain probes by writing files into the game root, which a running game's folder must never see.
    private Func<GamePath, DeploymentBackendId?>? AvailableFor(GameInstallation installation)
        => launchSession.IsRunning(installation) ? null : root => selector.Choose(installation, root).Backend;
}
