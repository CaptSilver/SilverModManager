using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Games.FileHashes;
using NexusMods.Abstractions.Games.FileHashes.Models;
using NexusMods.Abstractions.Steam;
using NexusMods.Abstractions.Steam.DTOs;
using NexusMods.Abstractions.Steam.Values;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.NexusModsApi;
using OperatingSystem = NexusMods.Abstractions.Games.FileHashes.Values.OperatingSystem;

namespace NexusMods.Games.FileHashes;

/// <summary>
/// Decides whether an installation's <see cref="LocalBaseline"/> is current, and builds it when it isn't.
/// <para>
/// A baseline describes the files for the locator ids the installation had when it was built, so it is
/// kept for as long as they are the same. The one exception is a Steam baseline that lacks depot manifests:
/// it is rebuilt once a manifest for one of the installed depots shows up in the depotcache that was
/// not there at the last build.
/// </para>
/// </summary>
internal sealed class LocalBaselineCoordinator : IDisposable
{
    private readonly Func<IConnection> _localStore;
    private readonly LocalVanillaBaseline _baseline;
    private readonly IDepotManifestSource? _manifestSource;
    private readonly ILogger<LocalBaselineCoordinator> _logger;
    private readonly ConcurrentDictionary<(GameStore Store, string Path), Lazy<Task<LocalBaselineResult>>> _inFlight = new();

    /// <summary>
    /// Builds run on this token rather than the first caller's, because later callers share the build and
    /// one caller giving up must not fail the others. Cancelled when the coordinator is disposed.
    /// </summary>
    private readonly CancellationTokenSource _shutdown = new();

    /// <param name="localStore">The connection the baseline rows are written to, read on each call so it is never held across a replacement.</param>
    /// <param name="manifestSource">Null when no Steam services are registered; every baseline is then a snapshot.</param>
    public LocalBaselineCoordinator(Func<IConnection> localStore, LocalVanillaBaseline baseline, IDepotManifestSource? manifestSource, ILogger<LocalBaselineCoordinator> logger)
    {
        _localStore = localStore;
        _baseline = baseline;
        _manifestSource = manifestSource;
        _logger = logger;
    }

    public async Task<LocalBaselineResult> Ensure(GameInstallation installation, CancellationToken cancellationToken = default)
    {
        var key = (installation.LocatorResult.Store, GamePathOf(installation));

        // The whole decision is shared, not only the build: two callers that both find no baseline would
        // otherwise each start one.
        var shared = _inFlight.GetOrAdd(key, k => new Lazy<Task<LocalBaselineResult>>(() => Run(k, installation)));
        return await shared.Value.WaitAsync(cancellationToken);
    }

    private async Task<LocalBaselineResult> Run((GameStore Store, string Path) key, GameInstallation installation)
    {
        try
        {
            // Everything below reads the disk synchronously until the first await; keep that off the caller's thread.
            await Task.Yield();
            return await EnsureCore(installation, _shutdown.Token);
        }
        finally
        {
            _inFlight.TryRemove(key, out _);
        }
    }

    private async Task<LocalBaselineResult> EnsureCore(GameInstallation installation, CancellationToken cancellationToken)
    {
        var locator = installation.LocatorResult;
        var gamePath = GamePathOf(installation);
        var store = _localStore();
        var existing = FindBaseline(store.Db, locator.Store, gamePath);

        if (locator.StoreUpdatePending)
        {
            // The files on disk may not match the manifest ids the store lists while it updates; the next start retries.
            _logger.LogInformation("{Game} is being updated by {Store}, not building its baseline now", installation.Game.DisplayName, locator.Store);
            return existing is { } current ? Describe(store.Db, current, rebuilt: false) : new LocalBaselineResult(LocalBaselineKind.Snapshot, false, 0);
        }

        if (existing is { } baseline && !IsStale(baseline, installation))
            return Describe(store.Db, baseline, rebuilt: false);

        var (manifests, allCovered) = ResolveManifests(installation);
        var synchronizer = installation.GetGame().Synchronizer;
        var request = new BaselineRequest
        {
            GameRoot = installation.Locations[LocationId.Game].Path,
            Store = locator.Store,
            AppId = AppIdOf(locator, manifests.Count),
            GamePath = gamePath,
            LocatorIds = locator.LocatorIds.Select(id => id.Value).ToArray(),
            VersionName = locator.StoreBuildId,
            Manifests = manifests,
            AllInstalledDepotsCovered = allCovered,
            IsIgnored = path => synchronizer.GamePathFilter.ShouldFilter(path) || synchronizer.IsIgnoredBackupPath(path),
            GameId = installation.Game.NexusModsGameId.HasValue ? installation.Game.NexusModsGameId.Value : NexusModsGameId.DefaultValue,
            OperatingSystem = OperatingSystemOf(locator),
        };

        var build = await _baseline.Build(request, store, cancellationToken);
        var built = LocalBaseline.Load(_localStore().Db, build.BaselineId);
        _logger.LogInformation("Built the {Kind} baseline for {Game}: {NonVanilla} files differ from the store's manifests", built.Kind, installation.Game.DisplayName, build.NonVanilla.Count);
        return new LocalBaselineResult(built.Kind, Rebuilt: true, build.NonVanilla.Count);
    }

    private static string GamePathOf(GameInstallation installation) => installation.Locations[LocationId.Game].Path.ToString();

    private static LocalBaseline.ReadOnly? FindBaseline(IDb db, GameStore store, string gamePath)
    {
        foreach (var baseline in LocalBaseline.FindByGamePath(db, gamePath))
        {
            if (baseline.Store == store) return baseline;
        }

        return null;
    }

    private static LocalBaselineResult Describe(IDb db, LocalBaseline.ReadOnly baseline, bool rebuilt)
        => new(baseline.Kind, rebuilt, NonVanillaFile.FindByBaseline(db, baseline.Id).Count());

    private bool IsStale(LocalBaseline.ReadOnly baseline, GameInstallation installation)
    {
        var locator = installation.LocatorResult;
        var current = locator.LocatorIds.Select(id => id.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!current.SetEquals(baseline.LocatorIds)) return true;
        if (baseline.Kind == LocalBaselineKind.DepotManifests) return false;
        if (_manifestSource is null || locator.Store != GameStore.Steam || locator.StoreRoot is not { } storeRoot) return false;

        // A manifest Steam cached after the build completes it. Only a file newer than the build counts: one
        // that was already there and still can't be read (its depot key is missing) would otherwise
        // force a rebuild at every start.
        var covered = baseline.Manifests.Select(m => m.ManifestId.Value).ToHashSet();
        foreach (var id in current)
        {
            if (!ulong.TryParse(id, out var manifestId) || covered.Contains(manifestId)) continue;
            if (!_manifestSource.TryGetManifestPath(storeRoot, ManifestId.From(manifestId), locator.Path, out var manifestPath)) continue;
            if (manifestPath.FileInfo.LastWriteTimeUtc > baseline.BuiltAt.UtcDateTime) return true;
        }

        return false;
    }

    /// <summary>
    /// Reads the cached manifest of every installed depot. Only Steam installs with a known store root have
    /// any; everything else is a snapshot.
    /// </summary>
    private (List<Manifest> Manifests, bool AllCovered) ResolveManifests(GameInstallation installation)
    {
        var locator = installation.LocatorResult;
        var manifests = new List<Manifest>();
        if (_manifestSource is null || locator.Store != GameStore.Steam || locator.StoreRoot is not { } storeRoot)
            return (manifests, false);

        var allCovered = true;
        foreach (var id in locator.LocatorIds.Select(id => id.Value).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!ulong.TryParse(id, out var value))
            {
                _logger.LogWarning("Locator id {LocatorId} of {Game} is not a Steam manifest id", id, installation.Game.DisplayName);
                allCovered = false;
                continue;
            }

            if (!_manifestSource.TryGetManifestPath(storeRoot, ManifestId.From(value), locator.Path, out var path))
            {
                _logger.LogInformation("Steam has not cached manifest {ManifestId} of {Game}", id, installation.Game.DisplayName);
                allCovered = false;
                continue;
            }

            switch (_manifestSource.Read(path))
            {
                case LocalManifestResult.Parsed parsed:
                    manifests.Add(parsed.Manifest);
                    break;
                case LocalManifestResult.NeedsDepotKey:
                    _logger.LogWarning("Manifest {Path} has encrypted file names and needs the depot key", path);
                    allCovered = false;
                    break;
                case LocalManifestResult.Unreadable unreadable:
                    _logger.LogWarning("Manifest {Path} could not be read: {Reason}", path, unreadable.Reason);
                    allCovered = false;
                    break;
                default:
                    throw new NotSupportedException($"Unhandled manifest result for {path}");
            }
        }

        return (manifests, allCovered);
    }

    private static AppId AppIdOf(GameLocatorResult locator, int manifestCount)
    {
        if (uint.TryParse(locator.StoreIdentifier, out var appId)) return AppId.From(appId);
        // Only the manifest rows carry the app id; a snapshot writes none.
        if (manifestCount == 0) return AppId.From(0);
        throw new InvalidOperationException($"Steam store identifier '{locator.StoreIdentifier}' is not an app id");
    }

    private static OperatingSystem OperatingSystemOf(GameLocatorResult locator)
        => locator.TargetOS.IsWindows ? OperatingSystem.Windows : locator.TargetOS.IsOSX ? OperatingSystem.MacOS : OperatingSystem.Linux;

    public void Dispose()
    {
        // Not disposed: a build still unwinding reads the token.
        _shutdown.Cancel();
    }
}
