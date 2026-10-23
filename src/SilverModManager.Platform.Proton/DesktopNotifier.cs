using CliWrap;
using Microsoft.Extensions.Logging;
using NexusMods.Sdk;

namespace NexusMods.Platform.Proton;

/// <summary>
/// Shows notifications through <c>notify-send</c>. A headless launch has no window, so this
/// is the only way to tell the user why a game did not start.
/// </summary>
public sealed class DesktopNotifier(IProcessRunner processRunner, ILogger<DesktopNotifier> logger) : IDesktopNotifier
{
    // notify-send returns as soon as the notification daemon accepts the message; a daemon
    // that does not answer must not hold up the launch or its teardown.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    /// <inheritdoc/>
    public void Notify(string summary, string body) => _ = NotifyAsync(summary, body);

    /// <summary>
    /// Same as <see cref="Notify"/>, but completes when <c>notify-send</c> has exited or given up.
    /// Never throws.
    /// </summary>
    public async Task NotifyAsync(string summary, string body)
    {
        using var cts = new CancellationTokenSource(Timeout);
        try
        {
            var command = Cli.Wrap("notify-send")
                .WithArguments(args => args
                    .Add($"--app-name={ApplicationConstants.ProductName}")
                    .Add("--urgency=critical")
                    .Add($"--icon={ApplicationConstants.ApplicationId}")
                    .Add(summary)
                    .Add(body)
                );

            await processRunner.RunAsync(command, cancellationToken: cts.Token);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not show desktop notification with notify-send: {Summary}", summary);
        }
    }
}
