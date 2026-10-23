using JetBrains.Annotations;

namespace SilverModManager.Networking.Thunderstore.DTOs;

/// <summary>
/// A package record from the community package list and its chunks. <see cref="Versions"/> is newest first.
/// </summary>
[PublicAPI]
public sealed record PackageListing
{
    public required string Name { get; init; }
    public required string FullName { get; init; }
    public string? Owner { get; init; }
    public string? PackageUrl { get; init; }
    public string? DonationLink { get; init; }
    public string? Uuid4 { get; init; }
    public DateTimeOffset? DateCreated { get; init; }
    public DateTimeOffset? DateUpdated { get; init; }
    public double RatingScore { get; init; }
    public bool IsPinned { get; init; }
    public bool IsDeprecated { get; init; }
    public bool HasNsfwContent { get; init; }
    public IReadOnlyList<string> Categories { get; init; } = [];
    public IReadOnlyList<PackageVersion> Versions { get; init; } = [];
}
