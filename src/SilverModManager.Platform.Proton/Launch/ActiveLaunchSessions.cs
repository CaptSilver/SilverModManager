using System.Reactive.Linq;
using System.Reactive.Subjects;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Platform.Proton.Launch;

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
    private readonly IServiceProvider? _services;
    private readonly object _lock = new();
    private readonly Dictionary<(GameStore Store, string StoreId), int> _counts = new();

    // Where each launch that reports one stands. A registration without a session (a forwarded Steam launch) has none.
    private readonly Dictionary<(GameStore Store, string StoreId), LaunchPhase> _phases = new();

    // Says which installation's count just changed. Subscribers read the count themselves.
    private readonly Subject<(GameStore, string)> _changes = new();

    /// <param name="services">
    /// Builds the sessions <see cref="BeginSession"/> hands out. Resolved lazily because a session depends on this
    /// registry. Null for a registry that only answers <see cref="IsRunning"/>.
    /// </param>
    public ActiveLaunchSessions(IServiceProvider? services = null) => _services = services;

    /// <inheritdoc />
    public bool IsRunning(GameInstallation installation)
    {
        lock (_lock) return _counts.ContainsKey(KeyOf(installation));
    }

    /// <summary>
    /// Whether <paramref name="installation"/> is running a launch: the current value on subscribe, then each
    /// change. Two registrations for one installation report false only after both are disposed.
    /// </summary>
    public IObservable<bool> WhenRunning(GameInstallation installation)
    {
        var key = KeyOf(installation);
        return Observable.Create<bool>(observer =>
        {
            // Each emission reads the count under the gate instead of carrying a value from the changing
            // thread, so two threads changing it at once cannot deliver their values out of order.
            var gate = new object();
            void Emit() { lock (gate) observer.OnNext(IsRunning(installation)); }
            var subscription = _changes.Where(changed => changed == key).Subscribe(_ => Emit());
            Emit();
            return subscription;
        }).DistinctUntilChanged();
    }

    /// <summary>
    /// The phase the launch of <paramref name="installation"/> is in (the current one on subscribe, then each
    /// change), or null when no launch reports one. The button's label follows it, so it does not claim the game
    /// is running while the loadout is still being applied.
    /// </summary>
    public IObservable<LaunchPhase?> WhenPhase(GameInstallation installation)
    {
        var key = KeyOf(installation);
        return Observable.Create<LaunchPhase?>(observer =>
        {
            var gate = new object();
            void Emit()
            {
                lock (gate)
                {
                    lock (_lock) observer.OnNext(_phases.TryGetValue(key, out var phase) ? phase : null);
                }
            }
            var subscription = _changes.Where(changed => changed == key).Subscribe(_ => Emit());
            Emit();
            return subscription;
        }).DistinctUntilChanged();
    }

    internal void ReportPhase(GameInstallation installation, LaunchPhase phase)
    {
        var key = KeyOf(installation);
        lock (_lock)
        {
            if (!_counts.ContainsKey(key)) return;
            _phases[key] = phase;
        }
        _changes.OnNext(key);
    }

    /// <inheritdoc />
    public IGameLaunch BeginSession(Loadout.ReadOnly loadout, LaunchSpec? runSpec = null)
    {
        return new GameLaunch(CreateSession(), new LaunchContext(loadout.InstallationInstance, loadout.LoadoutId, loadout.Name, WineDllOverrideRequirements.For(loadout.InstallationInstance.Game, loadout), runSpec));
    }

    internal LaunchSession CreateSession()
    {
        if (_services is null) throw new InvalidOperationException("This registry was created without a service provider and cannot start launches");
        return _services.GetRequiredService<LaunchSession>();
    }

    /// <summary>
    /// Marks <paramref name="installation"/> as running until the returned registration is disposed.
    /// </summary>
    public IDisposable Register(GameInstallation installation)
    {
        var key = KeyOf(installation);
        lock (_lock) _counts[key] = _counts.GetValueOrDefault(key) + 1;
        _changes.OnNext(key);
        return new Registration(this, key);
    }

    private static (GameStore, string) KeyOf(GameInstallation installation)
        => (installation.LocatorResult.Store, installation.LocatorResult.StoreIdentifier);

    private void Unregister((GameStore, string) key)
    {
        lock (_lock)
        {
            var remaining = _counts[key] - 1;
            if (remaining == 0)
            {
                _counts.Remove(key);
                _phases.Remove(key);
            }
            else _counts[key] = remaining;
        }
        _changes.OnNext(key);
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
