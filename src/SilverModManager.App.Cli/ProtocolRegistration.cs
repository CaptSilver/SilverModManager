using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using R3;
using SilverModManager.CLI.Types;
using SilverModManager.Sdk;
using SilverModManager.Sdk.EventBus;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.CLI;

/// <summary>
/// Claims the link schemes the user turned on. At start it refreshes a scheme the app still holds and leaves one
/// another app took alone; turning a setting on later takes the scheme right away.
/// </summary>
/// <remarks>
/// Another manager such as r2modman may claim its scheme again every time it starts, so taking it back at each of
/// our starts would be a tug-of-war with a toast each round. Only an explicit act takes a scheme.
/// </remarks>
public sealed class ProtocolRegistration(
    IEnumerable<IIpcProtocolHandler> handlers,
    IOSInterop osInterop,
    ISettingsManager settingsManager,
    IEventBus eventBus,
    ProtocolNotices notices,
    ILogger<ProtocolRegistration> logger) : BackgroundService
{
    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var handlerList = handlers.ToArray();

        foreach (var handler in handlerList)
        {
            if (handler.IsRegistrationWanted(settingsManager))
                await RegisterAsync(handler, UriSchemeClaim.IfUnclaimed, stoppingToken);
            else
                logger.LogInformation("Not registering as the {Scheme}:// handler: turn on its link setting or run the associate-{Scheme} command to do it", handler.Protocol, handler.Protocol);
        }

        // Turning a setting on later registers right away, so it needs no restart.
        using var subscriptions = new CompositeDisposable();
        foreach (var handler in handlerList)
        {
            handler.RegistrationTurnedOn(settingsManager)
                .SubscribeAwait(async (_, cancellationToken) => await RegisterAsync(handler, UriSchemeClaim.Take, cancellationToken), AwaitOperation.Sequential)
                .AddTo(subscriptions);
        }

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // host is stopping
        }
    }

    private async Task RegisterAsync(IIpcProtocolHandler handler, UriSchemeClaim claim, CancellationToken cancellationToken)
    {
        var scheme = handler.Protocol;
        try
        {
            var result = await osInterop.RegisterUriSchemeHandler(scheme, claim, cancellationToken: cancellationToken);
            Report(scheme, claim, result);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Exception while registering handler for {Scheme} links", scheme);
        }
    }

    private void Report(string scheme, UriSchemeClaim claim, UriSchemeRegistration result)
    {
        if (result.Registered)
        {
            if (claim is UriSchemeClaim.Take)
            {
                notices.Forget(scheme);
                if (result.OtherHandler is { } previous)
                    eventBus.Send(new CliMessages.ProtocolHandlerTaken(scheme, previous));
            }
            return;
        }

        if (result.OtherHandler is not { } holder) return;
        logger.LogInformation("{Scheme}:// links open {Holder}, leaving them there", scheme, holder);

        notices.ReportHeldElsewhere(scheme, holder);
    }
}
