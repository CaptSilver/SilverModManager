using JetBrains.Annotations;
using SilverModManager.Networking.Thunderstore.DTOs;
using SilverModManager.Sdk.Thunderstore;

namespace SilverModManager.Networking.Thunderstore;

/// <summary>
/// <see cref="IPackageLookup"/> answered from a <see cref="ThunderstoreCatalog"/>, so installing a package found
/// by searching the cached index needs no request.
/// </summary>
[PublicAPI]
public sealed class CatalogPackageLookup(ThunderstoreCatalog catalog) : IPackageLookup
{
    /// <inheritdoc/>
    public ValueTask<PackageDetail?> Lookup(ThunderstorePackageRef package, CancellationToken cancellationToken)
    {
        // Deprecated packages stay installable, as they are through the API.
        if (!catalog.TryGet(package, out var listing) || catalog.Latest(package, includeDeprecated: true) is not { } latest)
            return ValueTask.FromResult<PackageDetail?>(null);

        return ValueTask.FromResult<PackageDetail?>(new PackageDetail
        {
            Namespace = package.Namespace,
            Name = package.Name,
            FullName = listing.FullName,
            Owner = listing.Owner,
            PackageUrl = listing.PackageUrl,
            DonationLink = listing.DonationLink,
            DateCreated = listing.DateCreated,
            DateUpdated = listing.DateUpdated,
            RatingScore = listing.RatingScore,
            IsPinned = listing.IsPinned,
            IsDeprecated = listing.IsDeprecated,
            Categories = listing.Categories,
            Latest = latest with { Namespace = package.Namespace },
        });
    }

    /// <inheritdoc/>
    public ValueTask<PackageVersion?> LookupVersion(ThunderstoreVersionRef version, CancellationToken cancellationToken)
    {
        var found = catalog.Versions(version.Package).FirstOrDefault(v =>
            ThunderstoreVersionNumber.TryParse(v.VersionNumber, out var number) && number == version.Version);
        return ValueTask.FromResult(found is null ? null : found with { Namespace = version.Package.Namespace });
    }
}
