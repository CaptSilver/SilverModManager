using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.DataModel.SchemaVersions;

/// <summary>
/// Gives every disk state entry that holds only an xxHash3 an identity tagged with that kind.
/// </summary>
/// <remarks>
/// A disk state entry used to be its path, size, time and a bare xxHash3. It now also carries a <see cref="FileIdentity"/>,
/// so a digest of another algorithm can sit beside it without being mistaken for an xxHash3. The xxHash3 attribute
/// keeps its id and its value, because it is still present exactly when the identity is an xxHash3, so this adds a
/// datom per row and retracts nothing. The initial disk state is read as of a transaction from before this ran,
/// and that read has to keep working: it falls back to the xxHash3 for a row without an identity.
/// </remarks>
// ReSharper disable once InconsistentNaming
public sealed class _0004_TypedDiskStateIdentity(ILogger<_0004_TypedDiskStateIdentity> logger) : ITransactionalMigration
{
    public static (MigrationId Id, string Name) IdAndName => MigrationId.ParseNameAndId(nameof(_0004_TypedDiskStateIdentity));

    public Task Prepare(IDb db) => Task.CompletedTask;

    public void Migrate(ITransaction tx, IDb db)
    {
        var migrated = 0;
        foreach (var datom in db.Datoms(DiskStateEntry.XxHash3))
        {
            var entry = DiskStateEntry.Load(db, datom.E);
            if (DiskStateEntry.Identity.Contains(entry)) continue;
            if (!DiskStateEntry.XxHash3.TryGetValue(entry, out var hash)) continue;

            tx.Add(entry.Id, DiskStateEntry.Identity, FileIdentity.XxHash3(hash));
            migrated++;
        }

        logger.LogDebug("Gave {Count} disk state entries an xxHash3 identity", migrated);
    }
}
