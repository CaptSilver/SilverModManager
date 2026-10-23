using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Secrets;

namespace NexusMods.Secrets.Migration;

/// <summary>
/// Runs the registered migration steps against the persistent store, so a credential an older version
/// kept elsewhere ends up in the secret store.
/// </summary>
/// <remarks>
/// The steps get the store under the overlay, never the overlay itself: a key pinned by an environment
/// variable is still moved, so it is in the store the day the variable goes away.
/// </remarks>
public sealed class SecretMigrationRunner(IPersistentSecretStore store, IEnumerable<ISecretMigrationStep> steps, ILogger<SecretMigrationRunner> logger)
{
    /// <summary>
    /// Runs every step in registration order. A step that throws counts as deferred, so it runs again next start
    /// and the others still run.
    /// </summary>
    /// <returns>True when at least one step moved something.</returns>
    public async Task<bool> RunAsync(CancellationToken cancellationToken)
    {
        var anyMoved = false;
        foreach (var step in steps)
        {
            SecretMigrationOutcome outcome;
            try
            {
                outcome = await step.RunAsync(store, cancellationToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning(e, "Secret migration {Step} threw; it runs again next start", step.Name);
                outcome = new SecretMigrationOutcome.Deferred(e.Message);
            }

            switch (outcome)
            {
                case SecretMigrationOutcome.Moved:
                    anyMoved = true;
                    logger.LogInformation("Secret migration {Step}: Moved", step.Name);
                    break;
                case SecretMigrationOutcome.Deferred deferred:
                    logger.LogInformation("Secret migration {Step}: Deferred({Reason})", step.Name, deferred.Reason);
                    break;
                default:
                    logger.LogDebug("Secret migration {Step}: NothingToMove", step.Name);
                    break;
            }
        }

        return anyMoved;
    }
}
