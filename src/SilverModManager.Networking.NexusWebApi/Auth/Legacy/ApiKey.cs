using NexusMods.MnemonicDB.Abstractions;
using NexusMods.MnemonicDB.Abstractions.Attributes;
using NexusMods.MnemonicDB.Abstractions.Models;

namespace NexusMods.Networking.NexusWebApi.Auth;

/// <summary>
/// The row an older version kept the API key in. The key lives in the secret store now; this model stays
/// registered, with its attribute ids, so the migration can find and excise the rows, and so the schema
/// does not change. Nothing else should read or write it.
/// </summary>
public partial class ApiKey : IModelDefinition
{
    private const string Namespace = "SilverModManager.Networking.NexusWebApi.Auth.ApiKey";
    
    /// <summary>
    /// API Key storage for older Nexus Mods API calls
    /// </summary>
    public static readonly StringAttribute Key = new(Namespace, nameof(Key));

    /// <summary>
    /// Simple gett
    /// </summary>
    public static string? Get(IDb db)
    {
        var id = All(db).FirstOrDefault();
        return !id.IsValid() ? null : id.Key;
    }

    /// <summary>
    /// Set the key
    /// </summary>
    public static async Task Set(IConnection conn, string key)
    {
        using var tx = conn.BeginTransaction();
        var oldId = All(conn.Db).Select(ent => ent.Id).FirstOrDefault(tx.TempId());
        tx.Add(oldId, Key, key);
        await tx.Commit();
    }
}
