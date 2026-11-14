using SilverModManager.Sdk.Games;

namespace SilverModManager.Platform.Proton.Launch;

/// <summary>
/// A gate that can tell a mount left by a dead session from one a live run holds. The pending-finish step asks it when
/// no marker is set, since a session that died while preparing wrote none.
/// </summary>
public interface IStaleMountSource
{
    /// <summary>
    /// Whether a root of the installation's recorded plan is mounted and nothing in this process is serving it.
    /// </summary>
    bool HasStaleMount(GameInstallation installation);
}
