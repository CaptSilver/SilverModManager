using NexusMods.Hashing.xxHash3;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk.Games;

namespace NexusMods.Deployment.Vfs;

public sealed class VfsCatalog(IConnection connection) : IVfsCatalog
{
    public bool TryGetHash(GameInstallMetadataId installation, AbsolutePath path, FileFingerprint fingerprint, out Hash hash)
    {
        var entry = Find(connection.Db, installation, path);
        if (entry.IsValid() && FingerprintOf(entry) == fingerprint)
        {
            hash = entry.Hash;
            return true;
        }

        hash = default;
        return false;
    }

    // Reads the committed db for the existing row, as the deployment plan store does: the Record
    // signature carries no db. A path is recorded once per transaction.
    public void Record(ITransaction tx, GameInstallMetadataId installation, AbsolutePath path, FileFingerprint fingerprint, Hash hash)
    {
        var existing = Find(connection.Db, installation, path);
        if (existing.IsValid())
        {
            tx.Add(existing.Id, VfsCatalogEntry.Device, fingerprint.Device);
            tx.Add(existing.Id, VfsCatalogEntry.Inode, fingerprint.Inode);
            tx.Add(existing.Id, VfsCatalogEntry.Size, fingerprint.Size);
            tx.Add(existing.Id, VfsCatalogEntry.MTime, fingerprint.MTime);
            tx.Add(existing.Id, VfsCatalogEntry.CTime, fingerprint.CTime);
            tx.Add(existing.Id, VfsCatalogEntry.Hash, hash);
            return;
        }

        _ = new VfsCatalogEntry.New(tx)
        {
            InstallationId = installation,
            Path = path.ToString(),
            Device = fingerprint.Device,
            Inode = fingerprint.Inode,
            Size = fingerprint.Size,
            MTime = fingerprint.MTime,
            CTime = fingerprint.CTime,
            Hash = hash,
        };
    }

    private static VfsCatalogEntry.ReadOnly Find(IDb db, GameInstallMetadataId installation, AbsolutePath path)
    {
        var key = path.ToString();
        return VfsCatalogEntry.FindByInstallation(db, installation).FirstOrDefault(e => e.Path == key);
    }

    private static FileFingerprint FingerprintOf(VfsCatalogEntry.ReadOnly e) => new(e.Device, e.Inode, e.Size, e.MTime, e.CTime);
}
