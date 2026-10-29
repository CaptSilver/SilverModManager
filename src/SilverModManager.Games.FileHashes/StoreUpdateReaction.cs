using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Games.FileHashes;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.FileHashes;

/// <summary>What <see cref="StoreUpdateReaction.Check"/> found.</summary>
public enum StoreUpdateOutcome
{
    /// <summary>The store has the same build as the registry's copy.</summary>
    Unchanged,

    /// <summary>The store is writing the game's files; nothing was changed so the registry never holds a half-updated state.</summary>
    UpdateInProgress,

    /// <summary>The registry, the local baseline and the Health Check were brought up to the new build.</summary>
    Refreshed,

    /// <summary>The locator no longer finds the game.</summary>
    NotFound,
}

/// <summary>
/// Brings a running app up to date when a store changes a registered game's build: the registry's copy of the
/// installation (which every reader of <c>loadout.InstallationInstance</c> goes through), the local baseline of
/// the new build, and the Health Check. Merge trust and file-fingerprint caches are keyed by the registry's ids
/// and the baseline row, so they follow once those are right.
/// </summary>
/// <remarks>
/// Never takes the synchronizer's lock: it swaps one immutable registry entry, which an apply in flight has
/// already located fresh for itself, and asks the baseline coordinator, which serializes builds per folder.
/// </remarks>
public sealed class StoreUpdateReaction(
    IGameRegistry registry,
    IFileHashesService fileHashes,
    IDiagnosticManager diagnostics,
    IConnection connection,
    ILogger<StoreUpdateReaction> logger)
{
    // A second fire for the same folder (Steam rewrites the manifest many times) waits for the first.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _perFolder = new(StringComparer.Ordinal);

    /// <summary>
    /// Locates <paramref name="registered"/> again and refreshes everything that depends on its build if the
    /// store has settled on another one.
    /// </summary>
    public async Task<StoreUpdateOutcome> Check(GameInstallation registered, CancellationToken cancellationToken = default)
    {
        var gate = _perFolder.GetOrAdd(registered.LocatorResult.Path.ToString(), static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await CheckCore(registered, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<StoreUpdateOutcome> CheckCore(GameInstallation registered, CancellationToken cancellationToken)
    {
        var name = registered.Game.DisplayName;
        if (!registered.LocatorResult.Locator.TryLocate(registered.Game, out var fresh))
        {
            logger.LogDebug("{Game} is not found by its locator any more, leaving the registry as it is", name);
            return StoreUpdateOutcome.NotFound;
        }

        if (fresh.StoreUpdateInProgress)
        {
            logger.LogInformation("{Game} is being updated by {Store}; the app refreshes it when the update finishes", name, fresh.Store);
            return StoreUpdateOutcome.UpdateInProgress;
        }

        var before = registered.LocatorResult;
        // The registry's copy can have been taken mid-update or while Steam reported damage; settling on the same
        // build still has to clear those flags and let the skipped baseline be built.
        if (fresh.HasSameStoreStateAs(before)) return StoreUpdateOutcome.Unchanged;

        var located = new GameInstallation(fresh, registered.Locations);
        registry.RefreshInstallation(located);
        logger.LogInformation("{Game} was updated by {Store} to build {BuildId}; refreshing its file list", name, fresh.Store, fresh.StoreBuildId ?? "unknown");

        try
        {
            await fileHashes.EnsureLocalBaseline(located, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            // The next apply makes sure of the baseline again; the registry is already right.
            logger.LogWarning(e, "Could not rebuild the local baseline of {Game} after its update", name);
        }

        // A game the app never managed has no row, and so no loadout whose checks could change.
        if (registry.TryGetMetadata(located, out var row))
        {
            var db = connection.Db;
            foreach (var loadout in Loadout.All(db))
            {
                if (loadout.Installation.Id != row.Id || !loadout.IsVisible()) continue;
                try
                {
                    await diagnostics.RefreshAsync(loadout.LoadoutId);
                }
                catch (Exception e)
                {
                    logger.LogWarning(e, "Could not refresh the Health Check of {Loadout} after the update of {Game}", loadout.Name, name);
                }
            }
        }

        return StoreUpdateOutcome.Refreshed;
    }
}
