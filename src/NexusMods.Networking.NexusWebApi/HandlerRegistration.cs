using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NexusMods.Sdk;
using NexusMods.Sdk.Settings;
using R3;

namespace NexusMods.Networking.NexusWebApi;

internal class HandlerRegistration : BackgroundService
{
    private readonly ILogger _logger;
    private readonly IOSInterop _osInterop;
    private readonly ISettingsManager _settingsManager;

    public HandlerRegistration(
        ILogger<HandlerRegistration> logger,
        IOSInterop osInterop,
        ISettingsManager settingsManager)
    {
        _logger = logger;
        _osInterop = osInterop;
        _settingsManager = settingsManager;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_settingsManager.Get<NxmHandlerSettings>().RegisterAsDefaultHandler)
        {
            await RegisterAsync(stoppingToken);
        }
        else
        {
            _logger.LogInformation("Not registering as the nxm:// handler: turn on \"Handle nxm:// links\" in the settings or run the associate-nxm command to do it");
        }

        // Turning the setting on later registers right away, so it needs no restart.
        using var _ = _settingsManager
            .GetChanges<NxmHandlerSettings>()
            .Where(static settings => settings.RegisterAsDefaultHandler)
            .SubscribeAwait(async (_, cancellationToken) => await RegisterAsync(cancellationToken), AwaitOperation.Sequential);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // host is stopping
        }
    }

    private async Task RegisterAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _osInterop.RegisterUriSchemeHandler(scheme: "nxm", cancellationToken: cancellationToken);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Exception while registering handler for nxm links");
        }
    }
}
