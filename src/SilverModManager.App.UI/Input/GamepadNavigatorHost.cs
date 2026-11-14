using Microsoft.Extensions.Hosting;
using SilverModManager.Platform.Gamepad;

namespace SilverModManager.App.UI.Input;

/// <summary>Starts reading pads when the app starts and stops when it ends.</summary>
public sealed class GamepadNavigatorHost(IGamepadSource source) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        source.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        source.Stop();
        return Task.CompletedTask;
    }
}
