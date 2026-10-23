namespace SilverModManager.Abstractions.Loadouts.Deployment;

/// <summary>
/// Names a deployment backend. The value is persisted in plan rows, so it must stay stable.
/// </summary>
public readonly record struct DeploymentBackendId(string Value)
{
    /// <summary>
    /// Writes files into the game folder, the behaviour before backends were selectable.
    /// </summary>
    public static readonly DeploymentBackendId Copy = new("copy");

    /// <summary>
    /// Clones files out of the loose staging store with the filesystem's copy-on-write support.
    /// </summary>
    public static readonly DeploymentBackendId Reflink = new("reflink");

    /// <summary>
    /// Hard-links files out of the loose staging store.
    /// </summary>
    public static readonly DeploymentBackendId Hardlink = new("hardlink");

    /// <summary>
    /// Places symlinks into the loose staging store.
    /// </summary>
    public static readonly DeploymentBackendId Symlink = new("symlink");

    /// <summary>
    /// Serves the loadout through a FUSE mount that exists only while the app has the game launched, so the game
    /// folder holds none of the mods otherwise.
    /// </summary>
    public static readonly DeploymentBackendId Fuse = new("fuse");

    public override string ToString() => Value;
}
