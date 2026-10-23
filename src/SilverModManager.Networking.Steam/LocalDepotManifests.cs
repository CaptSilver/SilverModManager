using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Steam;
using NexusMods.Abstractions.Steam.Values;
using NexusMods.Paths;

namespace NexusMods.Networking.Steam;

/// <summary>
/// Reads the manifests the Steam client has already saved to disk.
/// </summary>
public sealed class LocalDepotManifests : IDepotManifestSource
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
}
