using SilverModManager.Sdk.Thunderstore;

namespace SilverModManager.App.UI.Pages.Thunderstore;

/// <summary>
/// One package of the community catalog as the page lists it. <see cref="Latest"/> is what Install adds; it is null
/// when the package has no version the page can name. <see cref="DisplayName"/> is the readable title and
/// <see cref="FullName"/> the raw <c>Owner-Name</c> id that search and install messages still use.
/// </summary>
public sealed record ThunderstorePackageRow(
    string FullName,
    string LatestVersion,
    double Rating,
    bool IsDeprecated,
    string Description,
    Uri? PackageUrl,
    ThunderstoreVersionRef? Latest = null,
    string Owner = "",
    string DisplayName = "",
    Uri? IconUrl = null,
    long Downloads = 0)
{
    /// <summary>"by glarmer", or nothing for a package without a known owner.</summary>
    public string OwnerLine => Owner == "" ? "" : $"by {Owner}";

    /// <summary>"4.0.2  ·  rating 112  ·  1.2M downloads", the small print under the title.</summary>
    public string Details => $"{LatestVersion}  ·  rating {Rating:0}  ·  {ThunderstoreRowText.CompactCount(Downloads)} downloads";
}
