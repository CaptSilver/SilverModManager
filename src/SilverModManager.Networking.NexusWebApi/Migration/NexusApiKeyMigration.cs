using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Secrets;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Networking.NexusWebApi.Auth;

namespace NexusMods.Networking.NexusWebApi.Migration;

/// <summary>
/// Moves the API key an older version kept in the database into the secret store, then excises the row.
/// </summary>
public sealed class NexusApiKeyMigration(IConnection connection, ILogger<NexusApiKeyMigration> logger) : ISecretMigrationStep
{
    public string Name => "nexus-api-key";

    public async Task<SecretMigrationOutcome> RunAsync(ISecretStore store, CancellationToken cancellationToken)
    {
        var ids = LegacyCredentialRows.FindAll(connection, ApiKey.Key);
        if (ids.Length == 0) return new SecretMigrationOutcome.NothingToMove();

        var db = connection.Db;
        var newest = LegacyCredentialRows.Newest(db, ApiKey.All(db).Select(row => row.Id));
        var key = newest is { } id && ApiKey.Load(db, id).Key is { Length: > 0 } value ? value : null;

        var outcome = await LegacyCredentialRows.MoveAsync(connection, store, NexusSecretKeys.ApiKey, key, ids, cancellationToken);
        if (outcome is SecretMigrationOutcome.Moved)
            logger.LogInformation("Moved the stored Nexus API key into the secret store and removed it from the database");
        return outcome;
    }
}
