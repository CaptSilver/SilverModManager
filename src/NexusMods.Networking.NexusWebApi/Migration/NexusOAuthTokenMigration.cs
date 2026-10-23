using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Secrets;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Networking.NexusWebApi.Auth;

namespace NexusMods.Networking.NexusWebApi.Migration;

/// <summary>
/// Moves the OAuth token an older version kept in the database into the secret store, then excises the row.
/// </summary>
public sealed class NexusOAuthTokenMigration(IConnection connection, ILogger<NexusOAuthTokenMigration> logger) : ISecretMigrationStep
{
    public string Name => "nexus-oauth-token";

    public async Task<SecretMigrationOutcome> RunAsync(ISecretStore store, CancellationToken cancellationToken)
    {
        // Any of the three attributes may be what is left of a half-removed row.
        var ids = new IAttribute[] { JWTToken.AccessToken, JWTToken.RefreshToken, JWTToken.ExpiresAt }
            .SelectMany(attribute => LegacyCredentialRows.FindAll(connection, attribute))
            .Distinct()
            .ToArray();
        if (ids.Length == 0) return new SecretMigrationOutcome.NothingToMove();

        var db = connection.Db;
        var newest = LegacyCredentialRows.Newest(db, JWTToken.All(db).Select(row => row.Id));
        string? json = null;
        if (newest is { } id)
        {
            var row = JWTToken.Load(db, id);
            json = new NexusOAuthToken(row.AccessToken, row.RefreshToken, row.ExpiresAt.ToUniversalTime()).ToJson();
        }

        var outcome = await LegacyCredentialRows.MoveAsync(connection, store, NexusSecretKeys.OAuth, json, ids, cancellationToken);
        if (outcome is SecretMigrationOutcome.Moved)
            logger.LogInformation("Moved the stored Nexus OAuth token into the secret store and removed it from the database");
        return outcome;
    }
}
