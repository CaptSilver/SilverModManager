using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.FileHashes;

/// <summary>
/// Watches the <c>appmanifest_*.acf</c> files of the Steam libraries that hold a registered game and asks
/// <see cref="StoreUpdateReaction"/> to look again when one changes, so an update Steam finishes while the app
/// runs is picked up without a restart. It parses nothing itself: the locator knows Steam's format, and the
/// reaction ignores a rewrite that changed nothing about the build. The registry is read again every
/// <see cref="DefaultRescanInterval"/> for libraries of games registered after startup. Steam only; other stores are located again
/// on each apply. Registered by the window-showing process only, like <see cref="LocalBaselineStartupPass"/>.
/// </summary>
internal sealed class SteamAppManifestWatcher : IHostedService, IDisposable
{
    // Steam rewrites the manifest many times during a download.
    internal static readonly TimeSpan DefaultDebounce = TimeSpan.FromSeconds(2);

    // How often the registry is read for a library that was not there at startup. The registry has no change event.
    internal static readonly TimeSpan DefaultRescanInterval = TimeSpan.FromSeconds(30);

    private readonly IGameRegistry _registry;
    private readonly StoreUpdateReaction _reaction;
    private readonly ILogger<SteamAppManifestWatcher> _logger;
    private readonly TimeSpan _debounce;
    private readonly TimeSpan _rescanInterval;
    private readonly CancellationTokenSource _stopping = new();
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly HashSet<string> _watchedLibraries = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();
    private Timer? _timer;
    private Timer? _rescan;
    private Task _running = Task.CompletedTask;

    public SteamAppManifestWatcher(IGameRegistry registry, StoreUpdateReaction reaction, ILogger<SteamAppManifestWatcher> logger)
        : this(registry, reaction, logger, DefaultDebounce) { }

    internal SteamAppManifestWatcher(IGameRegistry registry, StoreUpdateReaction reaction, ILogger<SteamAppManifestWatcher> logger, TimeSpan debounce, TimeSpan? rescanInterval = null)
    {
        _registry = registry;
        _reaction = reaction;
        _logger = logger;
        _debounce = debounce;
        _rescanInterval = rescanInterval ?? DefaultRescanInterval;
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
            _rescan = new Timer(_ => Rescan(), null, _rescanInterval, _rescanInterval);
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
                watcher.Changed += (_, _) => Poke();
                watcher.Created += (_, _) => Poke();
                watcher.Renamed += (_, _) => Poke();
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

    private void Poke()
    {
        lock (_lock)
        {
            if (_stopping.IsCancellationRequested) return;
            _timer ??= new Timer(_ => Fire(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _timer.Change(_debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void Fire()
    {
        lock (_lock)
        {
            _running = _running.ContinueWith(_ => CheckAll(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
        }
    }

    private async Task CheckAll()
    {
        foreach (var installation in _registry.LocateGameInstallations().Where(i => i.LocatorResult.Store == GameStore.Steam))
        {
            if (_stopping.IsCancellationRequested) return;
            try
            {
                await _reaction.Check(installation, _stopping.Token);
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
            _timer?.Dispose();
            _timer = null;
            _rescan?.Dispose();
            _rescan = null;
        }
    }
}
