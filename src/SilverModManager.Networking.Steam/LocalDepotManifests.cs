using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Steam;
using SilverModManager.Abstractions.Steam.Values;
using NexusMods.Paths;

namespace SilverModManager.Networking.Steam;

/// <summary>
/// Reads the manifests the Steam client has already saved to disk.
/// </summary>
public sealed class LocalDepotManifests : IDepotManifestCatalog
{
    private readonly DepotCache _cache;
    private readonly LocalManifestReader _reader;

    internal LocalDepotManifests(DepotCache cache, LocalManifestReader reader)
    {
        _cache = cache;
        _reader = reader;
    }

    public LocalDepotManifests(ILogger<LocalDepotManifests> logger)
        : this(new DepotCache(logger), new LocalManifestReader()) { }

    public bool TryGetManifestPath(AbsolutePath steamRoot, ManifestId manifestId, AbsolutePath? installPath, out AbsolutePath manifestPath) =>
        _cache.TryGetManifestPath(steamRoot, manifestId, installPath, out manifestPath);

    public LocalManifestResult Read(AbsolutePath manifestPath) => _reader.Read(manifestPath);

    public IReadOnlyList<AbsolutePath> CachedManifests(AbsolutePath steamRoot, AbsolutePath? installPath, DepotId depot) =>
        _cache.CachedManifests(steamRoot, installPath, depot);

    public ManifestFileHashes? ReadFileHashes(AbsolutePath manifestPath) => _reader.ReadHashes(manifestPath);
}
