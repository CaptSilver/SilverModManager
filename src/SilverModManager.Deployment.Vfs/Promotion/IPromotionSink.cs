using SilverModManager.Sdk.Loadouts;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.IO;

namespace SilverModManager.Deployment.Vfs;

/// <summary>A file the game wrote, at its mount-relative path.</summary>
public readonly record struct PromotedFile(RelativePath Path, Hash Hash, Size Size);

/// <summary>
/// Where staged changes go. Both calls must be safe to repeat with the same arguments: a
/// promotion that was interrupted is replayed from the journal.
/// </summary>
public interface IPromotionSink
{
    /// <summary>
    /// Stores file contents under the hash the caller gives. The caller has already checked the
    /// bytes against that hash; the store does not.
    /// </summary>
    Task Backup(IReadOnlyList<(Hash Hash, Size Size, IStreamFactory Stream)> files, CancellationToken token = default);

    /// <summary>
    /// Makes the loadout say that <paramref name="files"/> exist and <paramref name="deleted"/> do not,
    /// in one transaction.
    /// </summary>
    Task Record(LoadoutId loadout, IReadOnlyList<PromotedFile> files, IReadOnlyList<RelativePath> deleted, CancellationToken token = default);
}
