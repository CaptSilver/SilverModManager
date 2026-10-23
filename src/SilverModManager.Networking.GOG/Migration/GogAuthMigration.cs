using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Secrets;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Networking.GOG.Models.Legacy;

namespace SilverModManager.Networking.GOG.Migration;

/// <summary>
/// Moves the login an older version kept in the database into the secret store, then excises the row.
/// </summary>
public sealed class GogAuthMigration(IConnection connection, ILogger<GogAuthMigration> logger) : ISecretMigrationStep
{
    public string Name => "gog-oauth";

    public async Task<SecretMigrationOutcome> RunAsync(ISecretStore store, CancellationToken cancellationToken)
    {
        // Any of the attributes may be what is left of a half-removed row, and history includes rows that were
        // retracted but never excised, which a logout that crashed halfway leaves behind.
        var ids = new IAttribute[] { AuthInfo.AccessToken, AuthInfo.RefreshToken, AuthInfo.ExpiresAt, AuthInfo.SessionId, AuthInfo.UserId }
            .SelectMany(attribute => connection.History().Datoms(attribute).Select(datom => datom.E))
            .Distinct()
            .ToArray();
        if (ids.Length == 0) return new SecretMigrationOutcome.NothingToMove();

        var db = connection.Db;
        var newest = Newest(db, AuthInfo.All(db).Select(row => row.Id));
        string? json = null;
        if (newest is { } id)
        {
            var row = AuthInfo.Load(db, id);
            json = new GogCredential(row.AccessToken, row.RefreshToken, row.SessionId, row.UserId, row.ExpiresAt.ToUniversalTime()).ToJson();
        }

        if (json is not null)
        {
            var result = await store.SetAsync(GogSecretKeys.OAuth, json, cancellationToken);
            switch (result)
            {
                case SecretWriteResult.Written:
                    break;
                case SecretWriteResult.Unavailable unavailable:
                    return new SecretMigrationOutcome.Deferred($"{unavailable.Kind}: {unavailable.Reason}");
                default:
                    return new SecretMigrationOutcome.Deferred($"the store refused the write ({result})");
            }
        }

        // Excise rather than retract: a retract leaves the value in the history index.
        await connection.Excise(ids);
        logger.LogInformation("Moved the stored GOG login into the secret store and removed it from the database");
        return new SecretMigrationOutcome.Moved();
    }

    private static EntityId? Newest(IDb db, IEnumerable<EntityId> candidates)
    {
        EntityId? newest = null;
        var newestTx = default(TxId);
        foreach (var id in candidates)
        {
            var tx = db.Datoms(id).Max(datom => datom.T);
            if (newest is null || tx > newestTx)
            {
                newest = id;
                newestTx = tx;
            }
        }

        return newest;
    }
}
