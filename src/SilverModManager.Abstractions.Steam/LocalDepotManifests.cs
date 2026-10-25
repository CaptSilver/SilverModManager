using SilverModManager.Abstractions.Steam.DTOs;
using SilverModManager.Abstractions.Steam.Values;
using NexusMods.Paths;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.Abstractions.Steam;

/// <summary>
/// Finds and reads manifests the Steam client already saved to disk, so a game's file list can be
/// known without logging in to Steam.
/// </summary>
public interface IDepotManifestSource
{
    /// <summary>
    /// Finds the manifest Steam already saved for this manifest id: under the client root's depotcache, then under
    /// the install's own library (the install path's steamapps folder is its grandparent) when
    /// <paramref name="installPath"/> is given. Returns false when it is not cached.
    /// </summary>
    bool TryGetManifestPath(AbsolutePath steamRoot, ManifestId manifestId, AbsolutePath? installPath, out AbsolutePath manifestPath);

    /// <summary>
    /// Reads a depotcache manifest file.
    /// </summary>
    LocalManifestResult Read(AbsolutePath manifestPath);
}

/// <summary>
/// A manifest source that can also list the manifests Steam kept for a depot and read their file hashes without the
/// depot key. Steam leaves the manifests of older builds in the cache beside the current one, which is the only place
/// the hashes of a build that is no longer installed can come from.
/// </summary>
public interface IDepotManifestCatalog : IDepotManifestSource
{
    /// <summary>
    /// Every manifest of <paramref name="depot"/> in the client root's depotcache, then in the install's library
    /// depotcache (see <see cref="IDepotManifestSource.TryGetManifestPath"/>), each folder in file name order.
    /// </summary>
    IReadOnlyList<AbsolutePath> CachedManifests(AbsolutePath steamRoot, AbsolutePath? installPath, DepotId depot);

    /// <summary>
    /// The SHA-1 of every file in the manifest. The hashes sit in the part of a manifest that is not encrypted, so
    /// this works where <see cref="IDepotManifestSource.Read"/> answers <see cref="LocalManifestResult.NeedsDepotKey"/>.
    /// Null when the file is missing, truncated or not a manifest.
    /// </summary>
    ManifestFileHashes? ReadFileHashes(AbsolutePath manifestPath);
}

/// <summary>The file hashes of one cached manifest, without the file names.</summary>
public sealed record ManifestFileHashes(DepotId DepotId, ManifestId ManifestId, IReadOnlySet<Sha1Value> Hashes);

/// <summary>
/// The outcome of reading a manifest file from disk.
/// </summary>
public abstract record LocalManifestResult
{
    /// <summary>
    /// The file was read and its file names are usable.
    /// </summary>
    public sealed record Parsed(Manifest Manifest) : LocalManifestResult;

    /// <summary>
    /// The file names are encrypted. Only a logged-in Steam session can fetch the depot key that decrypts them.
    /// </summary>
    public sealed record NeedsDepotKey(DepotId DepotId, ManifestId ManifestId) : LocalManifestResult;

    /// <summary>
    /// The file is truncated, is not a manifest, or SteamKit could not parse it.
    /// </summary>
    public sealed record Unreadable(string Reason) : LocalManifestResult;
}
