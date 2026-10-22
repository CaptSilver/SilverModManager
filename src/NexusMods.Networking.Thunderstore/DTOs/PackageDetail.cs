using JetBrains.Annotations;

namespace NexusMods.Networking.Thunderstore.DTOs;

/// <summary>
/// A package from the experimental package endpoint. It carries only the newest version, not a version list.
/// </summary>
[PublicAPI]
public sealed record PackageDetail
{
    public required string Namespace { get; init; }
    public required string Name { get; init; }
    public string? FullName { get; init; }
    public string? Owner { get; init; }
    public string? PackageUrl { get; init; }
    public string? DonationLink { get; init; }
    public DateTimeOffset? DateCreated { get; init; }
    public DateTimeOffset? DateUpdated { get; init; }
    public double RatingScore { get; init; }
    public bool IsPinned { get; init; }
    public bool IsDeprecated { get; init; }
    public IReadOnlyList<string> Categories { get; init; } = [];
    public required PackageVersion Latest { get; init; }
    public IReadOnlyList<CommunityListing>? CommunityListings { get; init; }
}

/// <summary>
/// Where a package is listed: one entry per community it was published to.
/// </summary>
[PublicAPI]
public sealed record CommunityListing
{
    public required string Community { get; init; }
    public IReadOnlyList<string> Categories { get; init; } = [];
    public string? ReviewStatus { get; init; }
    public string? Url { get; init; }
}
