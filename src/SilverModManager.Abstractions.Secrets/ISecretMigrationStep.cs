namespace SilverModManager.Abstractions.Secrets;

/// <summary>
/// Moves one credential from where an older version kept it into the secret store.
/// </summary>
public interface ISecretMigrationStep
{
    /// <summary>
    /// Stable name, used in logs and to record that the step finished.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Runs the step against the persistent store. Must be safe to run again after <see cref="SecretMigrationOutcome.Deferred"/>.
    /// </summary>
    Task<SecretMigrationOutcome> RunAsync(ISecretStore store, CancellationToken cancellationToken);
}

/// <summary>
/// What a migration step did.
/// </summary>
public abstract record SecretMigrationOutcome
{
    private SecretMigrationOutcome() { }

    /// <summary>The old copy was written to the store and removed from its old location.</summary>
    public sealed record Moved : SecretMigrationOutcome;

    /// <summary>The old location held nothing.</summary>
    public sealed record NothingToMove : SecretMigrationOutcome;

    /// <summary>The store could not take the value yet; the old copy is untouched and the step runs again next start.</summary>
    public sealed record Deferred(string Reason) : SecretMigrationOutcome;
}
