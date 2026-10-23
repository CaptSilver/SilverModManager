using JetBrains.Annotations;
using SilverModManager.Networking.Thunderstore.DTOs;
using SilverModManager.Sdk.Thunderstore;

namespace SilverModManager.Networking.Thunderstore;

/// <summary>
/// Read-only access to the Thunderstore web API.
/// </summary>
[PublicAPI]
public interface IThunderstoreApi
{
    /// <summary>
    /// Fetches a package with its newest version and community listings, or null when it does not exist.
    /// </summary>
    Task<PackageDetail?> FetchPackage(ThunderstorePackageRef package, CancellationToken cancellationToken);

    /// <summary>
    /// Fetches one version of a package, or null when it does not exist.
    /// </summary>
    Task<PackageVersion?> FetchVersion(ThunderstoreVersionRef version, CancellationToken cancellationToken);

    /// <summary>
    /// Fetches the index of a community's package-listing chunks, verifying the blob against its URL.
    /// </summary>
    /// <exception cref="ArgumentException">The community is empty; the unscoped endpoint errors.</exception>
    Task<ListingIndex> FetchListingIndex(string community, CancellationToken cancellationToken);

    /// <summary>
    /// Streams the packages of one listing chunk, verifying the chunk against its URL before yielding any.
    /// </summary>
    IAsyncEnumerable<PackageListing> ReadChunk(Uri chunk, CancellationToken cancellationToken);

    /// <summary>
    /// Downloads a content-addressed blob exactly as served, without checking it. The caller verifies the bytes
    /// against the digest in the URL before trusting or storing them.
    /// </summary>
    Task<byte[]> DownloadBlob(Uri blob, CancellationToken cancellationToken);

    /// <summary>
    /// The URL Thunderstore serves a version's zip from (it redirects to the CDN).
    /// </summary>
    Uri DownloadUri(ThunderstoreVersionRef version);
}
