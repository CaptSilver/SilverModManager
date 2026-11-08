using System.Collections.Concurrent;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers;

/// <summary>
/// The file store's <see cref="IFileStore.ArchivesVersion"/> as each installation's last Apply in this process saw it.
/// The Apply status uses it to tell whether an archive may have come back since that Apply, whether or not anything
/// was watching the status in between. Nothing survives a restart: an installation with no record here has to be
/// compared once.
/// </summary>
public sealed class ArchiveVersionsAtApply
{
    private readonly ConcurrentDictionary<GameInstallMetadataId, (TxId Tx, long Version)> _versions = new();

    /// <summary>Records the store version an Apply read before committing the transaction <paramref name="appliedTx"/>.</summary>
    public void Record(GameInstallMetadataId installation, TxId appliedTx, long version) => _versions[installation] = (appliedTx, version);

    /// <summary>The store version recorded for the Apply committed in <paramref name="appliedTx"/>, or null when this process did not run it.</summary>
    public long? For(GameInstallMetadataId installation, TxId appliedTx)
        => _versions.TryGetValue(installation, out var seen) && seen.Tx == appliedTx ? seen.Version : null;
}
