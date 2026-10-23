using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.Networking.NexusWebApi.Auth;

namespace SilverModManager.Networking.NexusWebApi.Tests.Migration;

/// <summary>
/// The credential rows older versions kept in the database. Both migration tests work on the one shared
/// in-memory database, so they run one at a time and leave it empty.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public class LegacyCredentialCollection
{
    public const string Name = "Legacy credential rows";
}

internal static class LegacyCredentialSupport
{
    /// <summary>Excises every legacy credential row, current or retracted.</summary>
    public static async Task Clear(IConnection conn)
    {
        var ids = Ids(conn, ApiKey.Key).Concat(Ids(conn, JWTToken.AccessToken)).Distinct().ToArray();
        if (ids.Length > 0) await conn.Excise(ids);
    }

    public static EntityId[] Ids(IConnection conn, IAttribute attribute)
        => conn.History().Datoms(attribute).Select(d => d.E).Distinct().ToArray();

    public static async Task<EntityId> AddKey(IConnection conn, string key)
    {
        using var tx = conn.BeginTransaction();
        var id = tx.TempId();
        tx.Add(id, ApiKey.Key, key);
        var result = await tx.Commit();
        return result[id];
    }

    public static async Task<EntityId> AddToken(IConnection conn, string accessToken, string refreshToken, DateTimeOffset expiresAt)
    {
        using var tx = conn.BeginTransaction();
        var id = tx.TempId();
        tx.Add(id, JWTToken.AccessToken, accessToken);
        tx.Add(id, JWTToken.RefreshToken, refreshToken);
        tx.Add(id, JWTToken.ExpiresAt, expiresAt);
        var result = await tx.Commit();
        return result[id];
    }

    /// <summary>Retracts the entity without excising it, the state a crash between the two steps left behind.</summary>
    public static async Task Retract(IConnection conn, EntityId id)
    {
        using var tx = conn.BeginTransaction();
        tx.Delete(id, false);
        await tx.Commit();
    }
}
