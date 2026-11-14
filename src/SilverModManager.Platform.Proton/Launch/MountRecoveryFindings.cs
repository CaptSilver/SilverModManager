using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Platform.Proton.Launch;

/// <summary>
/// What the last recovery of each mounted root could not settle by itself: staged files with no loadout to go into,
/// files it set aside because they changed while being promoted, or more staged data than the promotion size cap. Kept until a later recovery of the root finds
/// nothing left, so a finding outlives the launch that produced it.
/// </summary>
public sealed class MountRecoveryFindings
{
    private readonly object _gate = new();
    private readonly Dictionary<(GameInstallMetadataId Installation, GamePath Root), MountRecoveryReport> _reports = [];

    /// <summary>
    /// Keeps the report when it needs the person's attention and forgets the root's earlier one when it does not.
    /// A report that says the game is still running changes nothing: the recovery did not look.
    /// </summary>
    public void Record(GameInstallMetadataId installation, GamePath root, MountRecoveryReport report)
    {
        if (report.Status == MountRecoveryStatus.StillRunning) return;

        lock (_gate)
        {
            if (report.Status is MountRecoveryStatus.NeedsTarget or MountRecoveryStatus.Blocked or MountRecoveryStatus.OverCap) _reports[(installation, root)] = report;
            else _reports.Remove((installation, root));
        }
    }

    public IReadOnlyList<(GamePath Root, MountRecoveryReport Report)> For(GameInstallMetadataId installation)
    {
        lock (_gate)
        {
            return _reports.Where(r => r.Key.Installation.Equals(installation)).Select(r => (r.Key.Root, r.Value)).ToArray();
        }
    }
}
