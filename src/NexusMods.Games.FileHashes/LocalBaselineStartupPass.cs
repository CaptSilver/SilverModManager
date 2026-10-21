using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Games.FileHashes;
using NexusMods.Sdk.Games;

namespace NexusMods.Games.FileHashes;

/// <summary>
/// Brings the local baseline of every managed game up to date after the app starts, because Steam
/// updates games while the app is closed. Registered by the window-showing process only: a headless
/// launch would compete with the game for disk reads, and a verbs-only process exits before a build ends.
/// </summary>
internal sealed class LocalBaselineStartupPass : IHostedService
{
    private readonly IFileHashesService _fileHashes;
    private readonly IGameRegistry _gameRegistry;
    private readonly ILogger<LocalBaselineStartupPass> _logger;
    private readonly CancellationTokenSource _stopping = new();
    private Task _pass = Task.CompletedTask;

    public LocalBaselineStartupPass(IFileHashesService fileHashes, IGameRegistry gameRegistry, ILogger<LocalBaselineStartupPass> logger)
    {
        _fileHashes = fileHashes;
        _gameRegistry = gameRegistry;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Hashing a large install takes minutes; startup must not wait for it.
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
            // Stopping is what cancelled it.
        }
    }

    /// <summary>
    /// Runs the pass to the end: one installation at a time, since each build reads its whole game directory.
    /// </summary>
    internal async Task Run(CancellationToken cancellationToken)
    {
        foreach (var installation in _gameRegistry.LocateGameInstallations())
        {
            if (cancellationToken.IsCancellationRequested) return;
            if (!_gameRegistry.TryGetMetadata(installation, out _)) continue;

            var path = installation.LocatorResult.Path;
            try
            {
                var result = await _fileHashes.EnsureLocalBaseline(installation, cancellationToken);
                _logger.LogInformation("Local baseline of {Game} at {Path}: {Kind}, {Rebuilt}, {NonVanilla} non-vanilla files",
                    installation.Game.DisplayName, path, result.Kind, result.Rebuilt ? "rebuilt" : "kept", result.NonVanillaCount);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Failed to update the local baseline of {Game} at {Path}", installation.Game.DisplayName, path);
            }
        }
    }
}
