namespace SilverModManager.Platform.Proton.Launch;

/// <summary>
/// Creates the lock that serialises launches of one game installation.
/// </summary>
public interface IInstallationLockFactory
{
    /// <summary>
    /// The lock for the installation of <paramref name="storeId"/> from <paramref name="store"/>. Nothing is
    /// taken until <see cref="InstallationLock.TryAcquire"/>.
    /// </summary>
    InstallationLock Create(string store, string storeId);
}
