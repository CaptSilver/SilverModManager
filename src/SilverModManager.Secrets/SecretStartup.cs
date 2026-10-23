using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Secrets;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Secrets.Migration;

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

    /// <param name="runInBackground">Starts the compaction off the calling thread. Tests substitute it to run the compaction by hand.</param>
    internal static async Task PrimeAsync(IServiceProvider services, CancellationToken cancellationToken, TimeSpan timeout, Action<Action>? runInBackground = null)
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

            if (services.GetService<SecretMigrationRunner>() is { } runner
                && await runner.RunAsync(cts.Token)
                && services.GetService<SecretStoreOptions>() is not { CompactAfterMove: false })
            {
                ScheduleCompaction(services, logger, runInBackground ?? (work => Task.Run(work)));
            }

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

    // An excised row leaves its bytes in the database files until the next compaction. It is only tidying, so it
    // runs once, off the start-up path, and a failure (a CLI main can close the database while it is in flight) is a log line.
    private static void ScheduleCompaction(IServiceProvider services, ILogger logger, Action<Action> runInBackground)
    {
        if (services.GetService<IConnection>() is not { } connection) return;

        runInBackground(() =>
        {
            try
            {
                connection.FlushAndCompact(verify: false).GetAwaiter().GetResult();
                logger.LogInformation("Compacted the database after moving credentials out of it");
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Compacting the database after moving credentials out of it failed");
            }
        });
    }
}
