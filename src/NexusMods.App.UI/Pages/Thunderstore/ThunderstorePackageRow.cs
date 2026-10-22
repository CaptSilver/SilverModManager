namespace NexusMods.App.UI.Pages.Thunderstore;

/// <summary>
/// One package of the community catalog as the page lists it.
/// </summary>
public sealed record ThunderstorePackageRow(
    string FullName,
    string LatestVersion,
    double Rating,
    bool IsDeprecated,
    string Description,
    Uri? PackageUrl);
