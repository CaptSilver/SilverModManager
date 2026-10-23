using NexusMods.Sdk.Thunderstore;

namespace NexusMods.App.UI.Pages.Thunderstore;

/// <summary>
/// One package of the community catalog as the page lists it. <see cref="Latest"/> is what Install adds; it is null
/// when the package has no version the page can name.
/// </summary>
public sealed record ThunderstorePackageRow(
    string FullName,
    string LatestVersion,
    double Rating,
    bool IsDeprecated,
    string Description,
    Uri? PackageUrl,
    ThunderstoreVersionRef? Latest = null);
