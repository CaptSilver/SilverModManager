using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.FileHashes;

/// <summary>
/// Watches the <c>appmanifest_*.acf</c> files of the Steam libraries that hold a registered game and asks
/// <see cref="StoreUpdateReaction"/> to look again when one changes, so an update Steam finishes while the app
/// runs is picked up without a restart. It parses nothing itself: the locator knows Steam's format, and the
/// reaction ignores a rewrite that changed nothing about the build. Each manifest is debounced on its own and only
/// the game it belongs to is looked at, and only when the app manages that install; a game Steam is still writing is
/// looked at at most once per <see cref="DefaultBusyInterval"/>. The registry's cached list is read again every
/// <see cref="DefaultRescanInterval"/> for libraries of games registered after startup; that locates nothing. Steam only; other stores are located again
/// on each apply. Registered by the window-showing process only, like <see cref="LocalBaselineStartupPass"/>.
/// </summary>
internal sealed class SteamAppManifestWatcher : IHostedService, IDisposable
{
    // Steam rewrites the manifest many times during a download.
    internal static readonly TimeSpan DefaultDebounce = TimeSpan.FromSeconds(2);

    // While a manifest says Steam is writing the game's files, Steam rewrites it for every chunk it downloads, so the
    // debounce alone still lets a look through every few seconds. The only change worth acting on is the one that
    // leaves "in progress", and waiting this long bounds how stale the registry's copy can be after that to the same
    // cadence as the library rescan.
    internal static readonly TimeSpan DefaultBusyInterval = TimeSpan.FromSeconds(30);

    // How often the registry is read for a library that was not there at startup. The registry has no change event.
    internal static readonly TimeSpan DefaultRescanInterval = TimeSpan.FromSeconds(30);

    private readonly IGameRegistry _registry;
    private readonly StoreUpdateReaction _reaction;
    private readonly ILogger<SteamAppManifestWatcher> _logger;
    private readonly TimeSpan _debounce;
    private readonly TimeSpan _rescanInterval;
    private readonly TimeSpan _busyInterval;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _stopping = new();
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly HashSet<string> _watchedLibraries = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();
    private readonly Dictionary<string, AppState> _apps = new(StringComparer.Ordinal);
    private ITimer? _rescan;
    private Task _running = Task.CompletedTask;

    public SteamAppManifestWatcher(IGameRegistry registry, StoreUpdateReaction reaction, ILogger<SteamAppManifestWatcher> logger)
        : this(registry, reaction, logger, DefaultDebounce) { }

    internal SteamAppManifestWatcher(IGameRegistry registry, StoreUpdateReaction reaction, ILogger<SteamAppManifestWatcher> logger, TimeSpan debounce, TimeSpan? rescanInterval = null, TimeSpan? busyInterval = null, TimeProvider? timeProvider = null)
    {
        _registry = registry;
        _reaction = reaction;
        _logger = logger;
        _debounce = debounce;
        _rescanInterval = rescanInterval ?? DefaultRescanInterval;
        _busyInterval = busyInterval ?? DefaultBusyInterval;
        _time = timeProvider ?? TimeProvider.System;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Locating games can take a while on first call; startup must not wait for it.
        _ = Task.Run(() =>
        {
            try
            {
                Watch();
            }
            catch (Exception e)
            {
                // Without this the watcher would silently never arm.
                _logger.LogError(e, "Could not start watching the Steam libraries for game updates; an update is noticed at the next apply or restart");
            }
            finally
            {
                StartRescanning();
            }
        }, CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync();
        Dispose();
        try
        {
            await _running.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Stopping is what cancelled it.
        }
    }

    // A game installed or a library added while the app runs reaches the registry after startup. Watch skips the
    // libraries it already holds, so a rescan only adds what is new.
    private void StartRescanning()
    {
        lock (_lock)
        {
            if (_stopping.IsCancellationRequested || _rescan is not null) return;
            _rescan = _time.CreateTimer(_ => Rescan(), null, _rescanInterval, _rescanInterval);
        }
    }

    private void Rescan()
    {
        if (_stopping.IsCancellationRequested) return;
        try
        {
            Watch();
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Could not look for new Steam libraries to watch for game updates");
        }
    }

    /// <summary>Resolves once every look scheduled so far has finished. Exposed for tests, which await it instead of sleeping.</summary>
    internal Task WhenIdle()
    {
        lock (_lock) return _running;
    }

    /// <summary>True once the library rescan timer is armed. Exposed for tests.</summary>
    internal bool IsRescanning
    {
        get { lock (_lock) return _rescan is not null; }
    }

    /// <summary>How many file watchers are armed. Exposed for tests.</summary>
    internal int WatcherCount
    {
        get { lock (_lock) return _watchers.Count; }
    }

    /// <summary>True once a watcher is armed on the <c>steamapps</c> folder <paramref name="library"/>. Exposed for tests, which wait on it instead of sleeping.</summary>
    internal bool IsWatching(string library)
    {
        lock (_lock) return _watchers.Any(w => w.Path == library && w.EnableRaisingEvents);
    }

    /// <summary>Starts a watcher on every Steam library that holds a registered game and is not watched yet. Exposed for tests, which await it.</summary>
    internal void Watch()
    {
        var libraries = _registry.LocateGameInstallations()
            .Where(installation => installation.LocatorResult.Store == GameStore.Steam)
            .Select(installation => LibraryOf(installation.LocatorResult.Path.ToString()))
            .OfType<string>()
            .Distinct(StringComparer.Ordinal);

        foreach (var library in libraries)
        {
            // Reserved under the same lock as the check, so an overlapping Watch skips it.
            lock (_lock)
            {
                if (!_watchedLibraries.Add(library)) continue;
            }

            var armed = false;
            try
            {
                var watcher = new FileSystemWatcher(library, "appmanifest_*.acf")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                    EnableRaisingEvents = false,
                };
                watcher.Changed += (_, e) => OnManifestChanged(e.FullPath);
                watcher.Created += (_, e) => OnManifestChanged(e.FullPath);
                watcher.Renamed += (_, e) => OnManifestChanged(e.FullPath);
                watcher.EnableRaisingEvents = true;
                lock (_lock)
                {
                    // Stopped while this one was being set up: nothing will dispose it later.
                    if (_stopping.IsCancellationRequested) watcher.Dispose();
                    else
                    {
                        _watchers.Add(watcher);
                        armed = true;
                    }
                }
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Cannot watch the Steam library at {Library} for game updates; an update is noticed at the next apply or restart", library);
            }
            finally
            {
                // Not armed (failed, or stopped): free the reservation so a later rescan can try again.
                if (!armed)
                {
                    lock (_lock) _watchedLibraries.Remove(library);
                }
            }
        }
    }

    /// <summary>The <c>steamapps</c> folder a game at <c>steamapps/common/&lt;dir&gt;</c> sits in, or null if it is somewhere else.</summary>
    private static string? LibraryOf(string gamePath)
    {
        var common = Path.GetDirectoryName(gamePath.TrimEnd('/'));
        var steamapps = common is null ? null : Path.GetDirectoryName(common);
        return steamapps is not null && Path.GetFileName(steamapps) == "steamapps" && Directory.Exists(steamapps) ? steamapps : null;
    }

    /// <summary>
    /// A write to <paramref name="fullPath"/> was seen. Does nothing unless it is an <c>appmanifest_&lt;id&gt;.acf</c>
    /// of a game in the registry; then the look at that game is scheduled after the debounce. Exposed for tests,
    /// which feed events in without a file system.
    /// </summary>
    internal void OnManifestChanged(string fullPath)
    {
        if (!TryAppIdOf(fullPath, out var appId)) return;
        // The registry hands back its cached list, so this costs nothing next to the look it may schedule.
        if (!_registry.LocateGameInstallations().Any(i => IsSteamApp(i, appId))) return;

        lock (_lock)
        {
            if (_stopping.IsCancellationRequested) return;
            if (!_apps.TryGetValue(appId, out var app)) _apps[appId] = app = new AppState();

            var now = _time.GetUtcNow();
            var delay = _debounce;
            if (app.Busy)
            {
                // Steam is still writing: the next look is due an interval after the last one, however many events come.
                if (app.Pending) return;
                var due = app.LastLook + _busyInterval - now;
                if (due > delay) delay = due;
            }

            if (app.Pending)
            {
                // A debounce that keeps being pushed back must not wait for ever for a quiet moment.
                var latest = app.PendingSince + _busyInterval - now;
                if (latest < delay) delay = latest;
            }
            else
            {
                app.PendingSince = now;
            }

            app.Pending = true;
            app.Timer ??= _time.CreateTimer(_ => Fire(appId), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            app.Timer.Change(delay < TimeSpan.Zero ? TimeSpan.Zero : delay, Timeout.InfiniteTimeSpan);
        }
    }

    private static bool TryAppIdOf(string path, out string appId)
    {
        appId = "";
        var name = Path.GetFileName(path);
        const string prefix = "appmanifest_", suffix = ".acf";
        if (!name.StartsWith(prefix, StringComparison.Ordinal) || !name.EndsWith(suffix, StringComparison.Ordinal)) return false;

        var digits = name.Substring(prefix.Length, name.Length - prefix.Length - suffix.Length);
        if (digits.Length == 0 || !digits.All(char.IsAsciiDigit)) return false;
        appId = digits;
        return true;
    }

    private static bool IsSteamApp(GameInstallation installation, string appId)
        => installation.LocatorResult.Store == GameStore.Steam && installation.LocatorResult.StoreIdentifier == appId;

    private void Fire(string appId)
    {
        lock (_lock)
        {
            if (_stopping.IsCancellationRequested || !_apps.TryGetValue(appId, out var app)) return;
            app.Pending = false;
            app.LastLook = _time.GetUtcNow();
            _running = _running.ContinueWith(_ => Check(appId, app), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
        }
    }

    private async Task Check(string appId, AppState app)
    {
        var busy = false;
        foreach (var installation in _registry.LocateGameInstallations().Where(i => IsSteamApp(i, appId)))
        {
            if (_stopping.IsCancellationRequested) return;
            // A game the app never managed has no loadout whose checks could change; the next apply locates it fresh.
            if (!_registry.TryGetMetadata(installation, out _)) continue;
            try
            {
                busy |= await _reaction.Check(installation, _stopping.Token) == StoreUpdateOutcome.UpdateInProgress;
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Failed to check {Game} after its Steam manifest changed", installation.Game.DisplayName);
            }
        }

        lock (_lock) app.Busy = busy;
    }

    /// <summary>What the watcher knows about one app's manifest. Guarded by the watcher's lock.</summary>
    private sealed class AppState
    {
        public ITimer? Timer;
        public bool Pending;
        public DateTimeOffset PendingSince;
        public DateTimeOffset LastLook;
        public bool Busy;
    }

    public void Dispose()
    {
        // A late event after disposal must not re-create the timer.
        try { _stopping.Cancel(); } catch (ObjectDisposedException) { }
        lock (_lock)
        {
            foreach (var watcher in _watchers) watcher.Dispose();
            _watchers.Clear();
            _watchedLibraries.Clear();
            foreach (var app in _apps.Values) app.Timer?.Dispose();
            _apps.Clear();
            _rescan?.Dispose();
            _rescan = null;
        }
    }
}
