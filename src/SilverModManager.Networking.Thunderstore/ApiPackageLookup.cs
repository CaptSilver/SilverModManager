using JetBrains.Annotations;
using NexusMods.Networking.Thunderstore.DTOs;
using NexusMods.Sdk.Thunderstore;

namespace NexusMods.Networking.Thunderstore;

/// <summary>
/// <see cref="IPackageLookup"/> backed by the Thunderstore web API.
/// </summary>
[PublicAPI]
public sealed class ApiPackageLookup(IThunderstoreApi api) : IPackageLookup
{
    /// <inheritdoc/>
    public async ValueTask<PackageDetail?> Lookup(ThunderstorePackageRef package, CancellationToken cancellationToken)
        => await api.FetchPackage(package, cancellationToken);

    /// <inheritdoc/>
    public async ValueTask<PackageVersion?> LookupVersion(ThunderstoreVersionRef version, CancellationToken cancellationToken)
        => await api.FetchVersion(version, cancellationToken);
}
