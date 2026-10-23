using NexusMods.Hashing.xxHash3;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk.Games;

namespace NexusMods.Deployment.Vfs;

/// <summary>
/// Remembers the hash of files under an installation's own directory so a file that has not changed
/// is not read again.
/// </summary>
public interface IVfsCatalog
{
    /// <summary>
    /// True if a hash was recorded for <paramref name="path"/> with exactly this fingerprint. A
    /// difference in any field is a miss: there is no partial trust.
    /// </summary>
    bool TryGetHash(GameInstallMetadataId installation, AbsolutePath path, FileFingerprint fingerprint, out Hash hash);

    /// <summary>Records the hash, replacing whatever was recorded for the same installation and path.</summary>
    void Record(ITransaction tx, GameInstallMetadataId installation, AbsolutePath path, FileFingerprint fingerprint, Hash hash);
}
