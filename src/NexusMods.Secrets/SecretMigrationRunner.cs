using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Secrets;

namespace NexusMods.Secrets;

/// <summary>
/// Runs the registered migration steps against the persistent store, so a credential an older version
/// kept elsewhere ends up in the secret store.
/// </summary>
public sealed class SecretMigrationRunner(IPersistentSecretStore store, IEnumerable<ISecretMigrationStep> steps, ILogger<SecretMigrationRunner> logger)
{
    /// <summary>
    /// Runs every step. A step that throws counts as deferred, so it runs again next start and the others still run.
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
                logger.LogWarning(e, "Secret migration step {Step} failed; it runs again next start", step.Name);
                outcome = new SecretMigrationOutcome.Deferred(e.Message);
            }

            switch (outcome)
            {
                case SecretMigrationOutcome.Moved:
                    anyMoved = true;
                    logger.LogInformation("Secret migration step {Step} moved its credential into the store", step.Name);
                    break;
                case SecretMigrationOutcome.Deferred deferred:
                    logger.LogInformation("Secret migration step {Step} deferred: {Reason}", step.Name, deferred.Reason);
                    break;
                default:
                    logger.LogDebug("Secret migration step {Step} had nothing to move", step.Name);
                    break;
            }
        }

        return anyMoved;
    }
}
