using JetBrains.Annotations;
using NexusMods.Networking.Thunderstore.DTOs;
using NexusMods.Sdk.Thunderstore;

namespace NexusMods.Networking.Thunderstore;

/// <summary>
/// Where the dependency resolver learns what a package or version contains. Kept apart from
/// <see cref="IThunderstoreApi"/> so resolution can be tested, and fed from a cache, without HTTP.
/// </summary>
[PublicAPI]
public interface IPackageLookup
{
    /// <summary>
    /// The package with its newest version, or null when it does not exist.
    /// </summary>
    ValueTask<PackageDetail?> Lookup(ThunderstorePackageRef package, CancellationToken cancellationToken);

    /// <summary>
    /// One version of a package, or null when it does not exist.
    /// </summary>
    ValueTask<PackageVersion?> LookupVersion(ThunderstoreVersionRef version, CancellationToken cancellationToken);
}
