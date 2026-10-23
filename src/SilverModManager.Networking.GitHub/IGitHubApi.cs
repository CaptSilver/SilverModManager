using System.Net.Http.Headers;
using JetBrains.Annotations;
using NexusMods.Networking.GitHub.DTOs;

namespace NexusMods.Networking.GitHub;

/// <summary>
/// Wrapper for the GitHub API.
/// </summary>
[PublicAPI]
public interface IGitHubApi
{
    /// <summary>
    /// Gets or sets the header to use for authentication.
    /// </summary>
    AuthenticationHeaderValue? AuthenticationHeader { get; set; }

    /// <summary>
    /// Fetches the latest releases.
    /// </summary>
    ValueTask<Release[]?> FetchReleases(string organization, string repository, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches the latest release.
    /// </summary>
    ValueTask<Release?> FetchLatestRelease(string organization, string repository, IComparer<Release>? comparer = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches the release with the given tag. Null when there is no such release or the request fails.
    /// </summary>
    ValueTask<Release?> FetchReleaseByTag(string organization, string repository, string tag, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches the release GitHub marks as latest: by default the newest non-draft, non-prerelease one. Unlike
    /// <see cref="FetchLatestRelease"/>, this does not order releases by tag, so a prerelease is never picked.
    /// </summary>
    ValueTask<Release?> FetchMarkedLatestRelease(string organization, string repository, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches the latest non-draft, non-prerelease release of the repository with the given numeric id, as
    /// GitHub itself defines "latest" (not by tag order).
    /// </summary>
    ValueTask<Release?> FetchLatestReleaseById(long repositoryId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches the release with the given tag from the repository with the given numeric id.
    /// </summary>
    ValueTask<Release?> FetchReleaseByTagById(long repositoryId, string tag, CancellationToken cancellationToken = default);
}
