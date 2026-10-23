using Microsoft.Extensions.Hosting;

namespace SilverModManager.Platform.Proton.Launch;

/// <summary>
/// A host lifetime that installs no signal handlers, so SIGTERM and SIGINT reach <see cref="StopHandler"/>
/// alone instead of also stopping the host underneath a running game.
/// </summary>
public sealed class NoSignalHostLifetime : IHostLifetime
{
    /// <inheritdoc />
    public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
