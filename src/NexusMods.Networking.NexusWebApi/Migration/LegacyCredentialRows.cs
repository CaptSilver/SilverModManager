using NexusMods.Abstractions.Secrets;
using NexusMods.MnemonicDB.Abstractions;

namespace NexusMods.Networking.NexusWebApi.Migration;

/// <summary>
/// The steps shared by the two migrations that lift a credential out of the database: find every row an
/// attribute ever had, store the newest live one, then excise them all.
/// </summary>
internal static class LegacyCredentialRows
{
    /// <summary>
    /// The entities that have, or once had, a value for <paramref name="attribute"/>. History includes rows
    /// that were retracted but never excised, which a logout that crashed halfway leaves behind.
    /// </summary>
    public static EntityId[] FindAll(IConnection connection, IAttribute attribute)
        => connection.History().Datoms(attribute).Select(datom => datom.E).Distinct().ToArray();

    /// <summary>
    /// The most recently written of <paramref name="candidates"/>, which are all live.
    /// </summary>
    public static EntityId? Newest(IDb db, IEnumerable<EntityId> candidates)
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

    /// <summary>
    /// Stores <paramref name="value"/> and, only when the store took it, excises <paramref name="ids"/>.
    /// A null <paramref name="value"/> means no live row: the ids are only excised.
    /// </summary>
    public static async Task<SecretMigrationOutcome> MoveAsync(
        IConnection connection,
        ISecretStore store,
        SecretKey key,
        string? value,
        EntityId[] ids,
        CancellationToken cancellationToken)
    {
        if (value is not null)
        {
            var result = await store.SetAsync(key, value, cancellationToken);
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
        return new SecretMigrationOutcome.Moved();
    }
}
