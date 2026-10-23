using NexusMods.Abstractions.Steam.DTOs;
using NexusMods.Abstractions.Steam.Values;
using NexusMods.Paths;

namespace NexusMods.Abstractions.Steam;

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
