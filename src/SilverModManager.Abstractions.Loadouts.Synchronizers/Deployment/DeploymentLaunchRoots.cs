using SilverModManager.Abstractions.Loadouts.Deployment;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;

/// <summary>
/// The host folders a process started for a game must be able to see so that what the recorded plan deployed
/// resolves: a symlink into the staging store dangles inside the Steam runtime container unless the store is shared.
/// </summary>
public sealed class DeploymentLaunchRoots(IDeploymentPlanStore plans, IEnumerable<IDeploymentBackend> backends, IConnection connection)
{
    private readonly Dictionary<DeploymentBackendId, IDeploymentBackend> _backends = backends.ToDictionary(b => b.Id);

    /// <summary>
    /// Every folder the backends of the recorded plan's roots require, once each, in ordinal order. A root recorded
    /// for a backend that isn't registered here contributes nothing; the apply reports that case.
    /// </summary>
    public IReadOnlyList<string> For(GameInstallation installation, GameInstallMetadataId id)
    {
        var folders = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var row in plans.Load(connection.Db, id).Roots)
        {
            if (!_backends.TryGetValue(row.Backend, out var backend)) continue;
            foreach (var folder in backend.RequiredFilesystems(installation, row.Root)) folders.Add(folder.ToString());
        }
        return folders.ToArray();
    }
}
