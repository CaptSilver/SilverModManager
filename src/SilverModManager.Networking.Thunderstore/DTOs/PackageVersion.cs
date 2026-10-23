using JetBrains.Annotations;

namespace NexusMods.Networking.Thunderstore.DTOs;

/// <summary>
/// One published version of a package. The package-listing chunks carry <see cref="FileSize"/> and no
/// <see cref="Namespace"/>; the experimental endpoints carry the reverse, so both are optional.
/// </summary>
[PublicAPI]
public sealed record PackageVersion
{
    public string? Namespace { get; init; }
    public required string Name { get; init; }
    public required string FullName { get; init; }
    public required string VersionNumber { get; init; }
    public string? Description { get; init; }
    public string? Icon { get; init; }
    public IReadOnlyList<string> Dependencies { get; init; } = [];
    public required string DownloadUrl { get; init; }
    public long Downloads { get; init; }
    public DateTimeOffset? DateCreated { get; init; }
    public string? WebsiteUrl { get; init; }
    public bool IsActive { get; init; } = true;
    public string? Uuid4 { get; init; }
    public long? FileSize { get; init; }
}
