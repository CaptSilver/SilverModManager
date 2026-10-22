using JetBrains.Annotations;
using NexusMods.Networking.Thunderstore.DTOs;
using NexusMods.Sdk.Thunderstore;

namespace NexusMods.Networking.Thunderstore;

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
    /// The URL Thunderstore serves a version's zip from (it redirects to the CDN).
    /// </summary>
    Uri DownloadUri(ThunderstoreVersionRef version);
}
