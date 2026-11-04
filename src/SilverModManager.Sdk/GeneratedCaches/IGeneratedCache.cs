using JetBrains.Annotations;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Sdk.GeneratedCaches;

/// <summary>
/// A cache a mod derives from the deployed game files, for example a decompressed copy of an archive. Nothing in the
/// cache checks itself against its source, so once an Apply changes the source files the cache has to go. One
/// implementation per mod.
/// </summary>
/// <remarks>
/// Implementations only answer questions; the synchronizer does the deleting, after an Apply succeeded. A cache is
/// the mod's own data, not the player's: the synchronizer never indexes, ingests or backs up the paths
/// <see cref="IsOwnedPath"/> claims.
/// </remarks>
[PublicAPI]
public interface IGeneratedCache
{
    /// <summary>The mod's name as the player knows it, for the log.</summary>
    string Name { get; }

    /// <summary>
    /// Whether the mod's cache can exist in the installation: the mod is deployed, was deployed until this Apply
    /// took it away, or left its cache behind. A cache left behind still has to be cleared, because the mod can come
    /// back and load an entry that no longer matches its archive.
    /// </summary>
    /// <param name="installation">The installation the Apply wrote to.</param>
    /// <param name="changed">Every game path the Apply added, replaced or removed.</param>
    bool AppliesTo(GameInstallation installation, IReadOnlyCollection<GamePath> changed);

    /// <summary>
    /// Whether the path is inside the cache's default folder in the game folder. Decided by name alone, without
    /// touching the disk, so it can serve as an indexing filter.
    /// </summary>
    bool IsOwnedPath(GamePath path);

    /// <summary>
    /// The folders the cache lives in for this installation, where the mod's ini points them (a folder the ini names
    /// replaces the default). They may not exist.
    /// </summary>
    IReadOnlyList<AbsolutePath> OwnedFolders(GameInstallation installation);

    /// <summary>
    /// The files and folders to delete because the Apply changed <paramref name="changed"/>. Only items that exist
    /// are returned, and a cache that a change leaves valid returns none.
    /// </summary>
    /// <param name="installation">The installation the Apply wrote to.</param>
    /// <param name="changed">Every game path the Apply added, replaced or removed.</param>
    IReadOnlyList<AbsolutePath> StaleItems(GameInstallation installation, IReadOnlyCollection<GamePath> changed);
}
