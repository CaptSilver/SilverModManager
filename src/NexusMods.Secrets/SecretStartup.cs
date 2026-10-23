using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Secrets;

namespace NexusMods.Secrets;

/// <summary>
/// The warm-up a process runs once at start: look at the store, move credentials from where older versions
/// kept them, and let the caches load. Nothing here may prompt, fail the start or take more than a few seconds.
/// </summary>
public static class SecretStartup
{
    private static readonly TimeSpan OverallTimeout = TimeSpan.FromSeconds(5);

    public static Task PrimeAsync(IServiceProvider services, CancellationToken cancellationToken)
        => PrimeAsync(services, cancellationToken, OverallTimeout);

    internal static async Task PrimeAsync(IServiceProvider services, CancellationToken cancellationToken, TimeSpan timeout)
    {
        var logger = services.GetService<ILoggerFactory>()?.CreateLogger(typeof(SecretStartup)) ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        try
        {
            // The overlay's status is what the user sees; Describe never prompts, so a locked keyring costs time, not a dialog.
            if (services.GetService<ISecretStore>() is { } store)
            {
                var status = await store.DescribeAsync(cts.Token);
                logger.LogInformation("Secret store: {Backend} ({Detail}), locked {Locked}, unencrypted {Unencrypted}", status.Backend, status.Detail, status.Locked, status.Unencrypted);
            }

            if (services.GetService<SecretMigrationRunner>() is { } runner)
                await runner.RunAsync(cts.Token);

            foreach (var primable in services.GetServices<ISecretPrimable>())
            {
                try
                {
                    await primable.PrimeAsync(cts.Token);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    logger.LogWarning(e, "Priming {Primable} failed", primable.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            logger.LogWarning("Secret store start-up did not finish within {Timeout}; the app starts anyway and the credentials load on first use", timeout);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Secret store start-up failed; the app starts anyway");
        }
    }
}
