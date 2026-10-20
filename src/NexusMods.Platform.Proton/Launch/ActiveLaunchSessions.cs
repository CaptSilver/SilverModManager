using NexusMods.Abstractions.Games;
using NexusMods.Sdk.Games;

namespace NexusMods.Platform.Proton.Launch;

/// <summary>
/// The installations this process holds a launch lock for. A launch registers itself once it has the lock and
/// disposes the registration after releasing it, so <see cref="IsRunning"/> covers every phase in between.
/// </summary>
/// <remarks>
/// Keyed on store and store id, like the lock itself, because <see cref="GameInstallation"/> compares by its
/// locator result's identity and the writers get a different instance from the game registry after a rescan.
/// </remarks>
public sealed class ActiveLaunchSessions : IGameLaunchSession
{
    private readonly object _lock = new();
    private readonly Dictionary<(GameStore Store, string StoreId), int> _counts = new();

    /// <inheritdoc />
    public bool IsRunning(GameInstallation installation)
    {
        lock (_lock) return _counts.ContainsKey(KeyOf(installation));
    }

    /// <summary>
    /// Marks <paramref name="installation"/> as running until the returned registration is disposed.
    /// </summary>
    public IDisposable Register(GameInstallation installation)
    {
        var key = KeyOf(installation);
        lock (_lock) _counts[key] = _counts.GetValueOrDefault(key) + 1;
        return new Registration(this, key);
    }

    private static (GameStore, string) KeyOf(GameInstallation installation)
        => (installation.LocatorResult.Store, installation.LocatorResult.StoreIdentifier);

    private void Unregister((GameStore, string) key)
    {
        lock (_lock)
        {
            var remaining = _counts[key] - 1;
            if (remaining == 0) _counts.Remove(key);
            else _counts[key] = remaining;
        }
    }

    private sealed class Registration(ActiveLaunchSessions owner, (GameStore, string) key) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Unregister(key);
        }
    }
}
