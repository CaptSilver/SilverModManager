using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Steam;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.FromSoft.Heal;

/// <summary>
/// Saves the Steam manifests of every managed game once the app has started, because Steam prunes its depotcache
/// while the app is closed and a manifest has to be read before that to be kept. Registered by the window-showing
/// process only, like the local baseline pass.
/// </summary>
internal sealed class SavedManifestsStartupPass(IGameRegistry gameRegistry, IDepotManifestSource? source, SavedManifestHashes saved, ILogger<SavedManifestsStartupPass> logger) : IHostedService
{
    private readonly CancellationTokenSource _stopping = new();
    private Task _pass = Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _pass = Task.Run(() => Run(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync();
        try
        {
            await _pass.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // The host gave up waiting; the pass stops at its next installation.
        }
    }

    /// <summary>Runs the pass to the end: one installation at a time.</summary>
    internal void Run(CancellationToken cancellationToken)
    {
        if (source is null) return;

        foreach (var installation in gameRegistry.LocateGameInstallations())
        {
            if (cancellationToken.IsCancellationRequested) return;
            try
            {
                var count = saved.Capture(source, installation);
                if (count > 0) logger.LogInformation("Saved {Count} Steam manifest(s) of {Game}", count, installation.Game.DisplayName);
            }
            catch (Exception e)
            {
                logger.LogError(e, "Failed to save the Steam manifests of {Game}", installation.Game.DisplayName);
            }
        }
    }
}
