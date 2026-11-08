using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Wabbajack.Gallery;
using SilverModManager.Wabbajack.Models;

namespace SilverModManager.Wabbajack.Diagnostics;

/// <summary>
/// Reruns the diagnostics of every loadout that holds a modlist after the gallery was fetched. The update emitter reads
/// only the cached listing, and nothing in the database changes when a fetch rewrites it, so without this a new release
/// would only show up once something else asked for a rerun.
/// </summary>
public sealed class GalleryRefreshDiagnosticsRunner(IWabbajackGalleryClient gallery, IConnection connection, IDiagnosticManager diagnostics, ILogger<GalleryRefreshDiagnosticsRunner> logger) : IHostedService, IDisposable
{
    private IDisposable? _subscription;
    private readonly CancellationTokenSource _closing = new();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_subscription is not null) throw new InvalidOperationException("The gallery refresh watcher is already running.");

        // On the thread pool: the refresh must not run inside the fetch that emits.
        _subscription = gallery.Refreshed.Subscribe(listing => { _ = Task.Run(RefreshAll); });
        return Task.CompletedTask;
    }

    private async Task RefreshAll()
    {
        LoadoutId[] loadouts;
        try
        {
            loadouts = WabbajackModlistLoadoutGroup.All(connection.Db)
                .Where(group => group.IsValid())
                .Select(group => group.AsCollectionGroup().AsLoadoutItemGroup().AsLoadoutItem().LoadoutId)
                .Distinct()
                .ToArray();
        }
        catch (Exception e) when (!_closing.IsCancellationRequested)
        {
            logger.LogWarning(e, "Listing the loadouts that hold a modlist after a gallery fetch failed");
            return;
        }

        // One loadout failing to refresh must not leave the ones after it showing a stale answer.
        foreach (var loadout in loadouts)
        {
            if (_closing.IsCancellationRequested) return;
            try
            {
                await diagnostics.RefreshAsync(loadout);
            }
            catch (Exception e) when (!_closing.IsCancellationRequested)
            {
                logger.LogWarning(e, "Refreshing the diagnostics of loadout {Loadout} after a gallery fetch failed", loadout);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        _subscription = null;
        _closing.Cancel();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _subscription?.Dispose();
        _closing.Dispose();
    }
}
