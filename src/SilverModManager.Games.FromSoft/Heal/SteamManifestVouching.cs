using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using SilverModManager.Abstractions.Steam;
using SilverModManager.Abstractions.Steam.Values;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.Games.FromSoft.Heal;

/// <summary>The Steam depot manifest that lists a file.</summary>
public sealed record ManifestMatch(ManifestId ManifestId, DepotId DepotId);

/// <summary>Tells whether Steam itself shipped a given file, which is what lets the app call a regulation the game's own.</summary>
public interface IManifestVouching
{
    /// <summary>The manifest of <paramref name="installation"/>'s game that lists a file with this SHA-1, or null when none the app can read does.</summary>
    ManifestMatch? Find(GameInstallation installation, Sha1Value hash);
}

/// <summary>
/// Looks a SHA-1 up in the depot manifests the Steam client cached. The install's own manifests are read first, then
/// every other manifest Steam kept for the same depots: a build that is no longer installed is only listed in the
/// manifest of its day. A manifest is matched by file hash alone, so it does not matter whether its file names are
/// encrypted. Anything that cannot be read is skipped; not being able to vouch is an answer, not a failure.
/// </summary>
/// <param name="source">Null when the app has no Steam manifest support, in which case nothing is ever vouched for.</param>
public sealed class SteamManifestVouching(IDepotManifestSource? source, ILogger<SteamManifestVouching> logger) : IManifestVouching
{
    /// <inheritdoc />
    public ManifestMatch? Find(GameInstallation installation, Sha1Value hash)
    {
        var locator = installation.LocatorResult;
        if (source is null || locator.Store != GameStore.Steam || locator.StoreRoot is not { } storeRoot) return null;

        var read = new HashSet<string>(StringComparer.Ordinal);
        var depots = new List<DepotId>();
        foreach (var id in locator.LocatorIds.Select(i => i.Value).Distinct(StringComparer.Ordinal))
        {
            if (!ulong.TryParse(id, out var value) || !source.TryGetManifestPath(storeRoot, ManifestId.From(value), locator.Path, out var path)) continue;

            read.Add(path.ToString());
            if (HashesOf(source, path) is not { } hashes) continue;
            if (hashes.Hashes.Contains(hash)) return new ManifestMatch(hashes.ManifestId, hashes.DepotId);
            if (!depots.Contains(hashes.DepotId)) depots.Add(hashes.DepotId);
        }

        if (source is not IDepotManifestCatalog catalog) return null;
        foreach (var depot in depots)
        {
            foreach (var path in Cached(catalog, storeRoot, locator.Path, depot))
            {
                if (!read.Add(path.ToString())) continue;
                if (HashesOf(source, path) is { } hashes && hashes.Hashes.Contains(hash)) return new ManifestMatch(hashes.ManifestId, hashes.DepotId);
            }
        }
        return null;
    }

    private IReadOnlyList<AbsolutePath> Cached(IDepotManifestCatalog catalog, AbsolutePath storeRoot, AbsolutePath installPath, DepotId depot)
    {
        try
        {
            return catalog.CachedManifests(storeRoot, installPath, depot);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "The cached manifests of depot {Depot} could not be listed", depot.Value);
            return [];
        }
    }

    private ManifestFileHashes? HashesOf(IDepotManifestSource manifests, AbsolutePath path)
    {
        if (manifests is IDepotManifestCatalog catalog) return catalog.ReadFileHashes(path);

        switch (manifests.Read(path))
        {
            case LocalManifestResult.Parsed parsed:
                return new ManifestFileHashes(parsed.Manifest.DepotId, parsed.Manifest.ManifestId, parsed.Manifest.Files.Select(f => f.Hash).ToHashSet());
            case LocalManifestResult.NeedsDepotKey:
                logger.LogDebug("Manifest {Path} has encrypted file names and this source cannot read its hashes without the depot key", path);
                return null;
            case LocalManifestResult.Unreadable unreadable:
                logger.LogDebug("Manifest {Path} could not be read: {Reason}", path, unreadable.Reason);
                return null;
            default:
                throw new NotSupportedException($"Unhandled manifest result for {path}");
        }
    }
}
