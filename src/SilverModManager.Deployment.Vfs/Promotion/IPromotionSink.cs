using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Loadouts;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.IO;

namespace SilverModManager.Deployment.Vfs;

/// <summary>A file the game wrote, at its mount-relative path.</summary>
public readonly record struct PromotedFile(RelativePath Path, FileIdentity Identity, Size Size);

/// <summary>
/// Where staged changes go. Both calls must be safe to repeat with the same arguments: a
/// promotion that was interrupted is replayed from the journal.
/// </summary>
public interface IPromotionSink
{
    /// <summary>
    /// Stores file contents under the identity the caller gives. The caller has already checked the
    /// bytes against that identity; the store does not.
    /// </summary>
    Task Backup(IReadOnlyList<(FileIdentity Identity, Size Size, IStreamFactory Stream)> files, CancellationToken token = default);

    /// <summary>
    /// Makes the loadout say that <paramref name="files"/> exist and <paramref name="deleted"/> do not,
    /// in one transaction.
    /// </summary>
    Task Record(LoadoutId loadout, IReadOnlyList<PromotedFile> files, IReadOnlyList<RelativePath> deleted, CancellationToken token = default);
}
