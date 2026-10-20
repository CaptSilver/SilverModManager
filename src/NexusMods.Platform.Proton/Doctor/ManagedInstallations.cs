using NexusMods.Abstractions.Loadouts;
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
internal sealed class LoadoutManagedInstallations(IConnection connection, IGameRegistry registry) : IManagedInstallations
{
    public IReadOnlyList<ManagedInstallation> List() => Loadout.All(connection.Db)
        .Where(loadout => loadout.IsVisible())
        .Select(loadout => (Loadout: loadout, Found: registry.TryGetGameInstallation(loadout, out var installation) ? installation : null))
        .Where(x => x.Found is not null)
        .GroupBy(x => x.Found!)
        .Select(group => new ManagedInstallation(group.Key, group.Select(x => x.Loadout.Name).Order(StringComparer.Ordinal).ToArray()))
        .ToArray();
}
