using SilverModManager.Sdk;

namespace SilverModManager.Platform.Proton.Launch;

/// <summary>
/// Puts the lock files under <c>XDG_RUNTIME_DIR</c>, which is per-user, tmpfs-backed and cleared on logout.
/// </summary>
internal sealed class InstallationLockFactory(IEnvironmentReader environment) : IInstallationLockFactory
{
    public InstallationLock Create(string store, string storeId)
    {
        var runtimeDir = environment.Get("XDG_RUNTIME_DIR");
        if (string.IsNullOrEmpty(runtimeDir))
            throw new InvalidOperationException("XDG_RUNTIME_DIR is not set, so there is nowhere to put the launch lock");

        return new InstallationLock(InstallationLock.PathFor(runtimeDir, store, storeId));
    }
}
